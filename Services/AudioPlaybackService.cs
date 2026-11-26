using System.Windows;
using NAudio.Wave;

namespace MusicPlayer;

public sealed class PlaybackErrorEventArgs(Exception exception) : EventArgs
{
    public Exception ErrorException { get; } = exception;
}

// Transport and preference setters run on the UI thread. Device events return
// through the dispatcher. Render settings are published by the sample provider.
public sealed class AudioPlaybackService
{
    private readonly Func<IWavePlayer> _createOutput;
    private readonly Action<Action> _dispatch;
    private Session? _session;
    private double _volume = 1;
    private bool _isMuted;
    private bool _eqEnabled = true;
    private float _trimDb;
    private readonly float[] _eqGains = new float[EqualizerSampleProvider.BandCount];

    public AudioPlaybackService()
        : this(() => new WaveOut(), action => Application.Current?.Dispatcher.BeginInvoke(action)) { }

    internal AudioPlaybackService(Func<IWavePlayer> createOutput, Action<Action> dispatch)
    {
        _createOutput = createOutput;
        _dispatch = dispatch;
    }

    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;
    public event EventHandler<PlaybackErrorEventArgs>? MediaFailed;

    public TimeSpan Position
    {
        get => _session?.Reader.CurrentTime ?? TimeSpan.Zero;
        set => Control(session =>
        {
            var reader = session.Reader;
            session.Eq.Reposition(() =>
            {
                // Duration expressed in ticks can round down by a partial frame.
                if (value >= reader.TotalTime)
                    reader.Position = reader.Length;
                else
                    reader.CurrentTime = value < TimeSpan.Zero ? TimeSpan.Zero : value;
            });
        });
    }

    public TimeSpan? Duration => _session?.Reader.TotalTime;

    public double Volume
    {
        get => _volume;
        set
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _volume = Math.Clamp(value, 0, 1);
            ApplyLevel();
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set { _isMuted = value; ApplyLevel(); }
    }

    public float OutputTrimDb
    {
        get => _trimDb;
        set
        {
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _trimDb = Math.Clamp(value, -24, 0);
            ApplyLevel();
        }
    }

    public bool OutputOverloaded => _session?.Eq.Overloaded ?? false;
    public void ClearOverload() => _session?.Eq.ClearOverload();

    private float OutputLevel => _isMuted ? 0 : (float)(_volume * Math.Pow(10, _trimDb / 20.0));
    private void ApplyLevel() => _session?.Eq.SetOutputLevel(OutputLevel);

    public bool EqualizerEnabled => _eqEnabled;
    public float GetBandGain(int band) => _eqGains[band];

    public void SetBandGain(int band, float gainDb)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)band, (uint)_eqGains.Length);
        float gain = EqualizerSampleProvider.ClampGain(gainDb);
        _eqGains[band] = gain;
        _session?.Eq.SetBandGain(band, gain);
    }

    internal void SetPreset(ReadOnlySpan<float> gains)
    {
        Span<float> validated = stackalloc float[EqualizerSampleProvider.BandCount];
        EqualizerSampleProvider.CopyValidatedGains(gains, validated);
        validated.CopyTo(_eqGains);
        _session?.Eq.SetBands(validated);
    }

    public void SetEqualizerEnabled(bool enabled)
    {
        _eqEnabled = enabled;
        _session?.Eq.SetEnabled(enabled);
    }

    public void ResetEqualizer()
    {
        SetPreset(new float[EqualizerSampleProvider.BandCount]);
        OutputTrimDb = 0;
        ClearOverload();
    }

    public void Open(Uri uri)
    {
        Close();
        try
        {
            var reader = new AudioFileReader(uri.LocalPath);
            IWavePlayer? output = null;
            try
            {
                var eq = new EqualizerSampleProvider(reader);
                eq.SetEnabled(_eqEnabled);
                eq.SetBands(_eqGains);
                eq.SetOutputLevel(OutputLevel);
                output = _createOutput();
                output.Init(eq.ToWaveProvider());
                _session = new Session(reader, eq, output, OnStopped);
            }
            catch
            {
                try { output?.Dispose(); }
                finally { reader.Dispose(); }
                throw;
            }
        }
        catch (Exception error)
        {
            Report(error);
            return;
        }
        MediaOpened?.Invoke(this, EventArgs.Empty);
    }

    private void OnStopped(Session session, StoppedEventArgs args)
    {
        int version = session.TransportVersion;
        _dispatch(() =>
        {
            if (!ReferenceEquals(_session, session))
                return;
            if (args.Exception is { } error)
            {
                FailSession(error);
            }
            else if (version == session.TransportVersion && session.Reader.Position >= session.Reader.Length)
                MediaEnded?.Invoke(this, EventArgs.Empty);
        });
    }

    public void Play() => Control(static session => session.Output.Play());
    public void Pause() => Control(static session => session.Output.Pause());
    public void Stop() => Control(static session => session.Stop());

    private void Control(Action<Session> command)
    {
        if (_session is not { } session)
            return;
        try
        {
            session.AdvanceTransport();
            command(session);
        }
        catch (Exception error)
        {
            FailSession(error);
        }
    }

    private void Report(Exception error) => MediaFailed?.Invoke(this, new PlaybackErrorEventArgs(error));

    private void FailSession(Exception error)
    {
        try { RetireSession(); }
        catch (Exception cleanupError) { error = new AggregateException(error, cleanupError); }
        Report(error);
    }

    public void Close()
    {
        try { RetireSession(); }
        catch (Exception error) { Report(error); }
    }

    private void RetireSession()
    {
        Session? session = _session;
        _session = null;
        session?.Close();
    }

    // A retired output may still be reading the previous file until its stop
    // notification. Release exactly once, including a delayed/duplicate event.
    private sealed class Session
    {
        internal AudioFileReader Reader { get; }
        internal EqualizerSampleProvider Eq { get; }
        internal IWavePlayer Output { get; }
        private readonly Action<Session, StoppedEventArgs> _stopped;
        private volatile bool _closing;
        private int _released;
        private int _transportVersion, _manualStop;
        internal int TransportVersion => Volatile.Read(ref _transportVersion);
        internal void AdvanceTransport() => Interlocked.Increment(ref _transportVersion);

        internal Session(AudioFileReader reader, EqualizerSampleProvider eq, IWavePlayer output,
            Action<Session, StoppedEventArgs> stopped)
        {
            Reader = reader;
            Eq = eq;
            Output = output;
            _stopped = stopped;
            Output.PlaybackStopped += Stopped;
        }

        private void Stopped(object? sender, StoppedEventArgs args)
        {
            if (_closing)
                Release();
            else if (Interlocked.Exchange(ref _manualStop, 0) == 0 || args.Exception != null)
                _stopped(this, args);
        }

        internal void Stop()
        {
            if (Output.PlaybackState == PlaybackState.Stopped)
                return;
            Volatile.Write(ref _manualStop, 1);
            Output.Stop();
        }

        internal void Close()
        {
            _closing = true;
            try
            {
                if (Output.PlaybackState != PlaybackState.Stopped)
                    Output.Stop();
                else
                    Release();
            }
            catch
            {
                Release();
                throw;
            }
        }

        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            Output.PlaybackStopped -= Stopped;
            try { Output.Dispose(); }
            finally { Reader.Dispose(); }
        }
    }
}
