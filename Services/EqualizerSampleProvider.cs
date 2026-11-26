using NAudio.Wave;

namespace MusicPlayer;

public sealed class EqualizerSampleProvider : ISampleProvider
{
    public static IReadOnlyList<float> Frequencies { get; } = Array.AsReadOnly<float>(
        [31.25f, 62.5f, 125, 250, 500, 1000, 2000, 4000, 8000, 16000]);
    public const int BandCount = 10;

    private readonly ISampleProvider _source;
    private readonly GraphicEqualizerDesign _design;
    private readonly object _settingsGate = new();
    private readonly float[] _gains = new float[BandCount];
    private bool _enabled = true;
    private volatile EqualizerCurve _requested = EqualizerCurve.Flat;
    private volatile float _requestedLevel = 1;
    private int _overloaded;

    // Seek and decoder reads share this gate only for source positioning. Settings
    // writers never acquire it. The single render thread owns both filter banks.
    private readonly object _decoderGate = new();
    private bool _resetPending = true;
    private FilterBank _current, _next;
    private readonly int _fadeFrames, _channels;
    private int _channel, _fadePosition, _levelFrames;
    private double _level = 1, _targetLevel = 1, _levelStep;

    public WaveFormat WaveFormat => _source.WaveFormat;
    internal bool Overloaded => Volatile.Read(ref _overloaded) != 0;
    internal void ClearOverload() => Interlocked.Exchange(ref _overloaded, 0);

    public EqualizerSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _design = new GraphicEqualizerDesign(WaveFormat.SampleRate);
        _channels = WaveFormat.Channels;
        _fadeFrames = Math.Max(1, (int)Math.Round(WaveFormat.SampleRate * 0.020));
        _current = new FilterBank(_channels);
        _next = new FilterBank(_channels);
    }

    internal static float ClampGain(float gainDb)
    {
        if (!float.IsFinite(gainDb))
            throw new ArgumentOutOfRangeException(nameof(gainDb));
        return Math.Clamp(gainDb, -12, 12);
    }

    public void SetBandGain(int band, float gainDb)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)band, (uint)BandCount);
        float gain = ClampGain(gainDb);
        lock (_settingsGate)
        {
            if (_gains[band] == gain)
                return;
            _gains[band] = gain;
            if (_enabled && band < _design.ActiveBandCount)
                Publish();
        }
    }

    internal void SetBands(ReadOnlySpan<float> gains)
    {
        Span<float> checkedGains = stackalloc float[BandCount];
        CopyValidatedGains(gains, checkedGains);
        lock (_settingsGate)
        {
            int active = _design.ActiveBandCount;
            bool changed = !checkedGains[..active].SequenceEqual(_gains.AsSpan(0, active));
            checkedGains.CopyTo(_gains);
            if (_enabled && changed)
                Publish();
        }
    }

    internal static void CopyValidatedGains(ReadOnlySpan<float> gains, Span<float> destination)
    {
        if (gains.Length != BandCount)
            throw new ArgumentException("A preset must contain ten gains.", nameof(gains));
        for (int i = 0; i < gains.Length; i++)
            destination[i] = ClampGain(gains[i]);
    }

    public void SetEnabled(bool enabled)
    {
        lock (_settingsGate)
        {
            if (_enabled == enabled)
                return;
            _enabled = enabled;
            Publish();
        }
    }

    private void Publish() => _requested = _enabled ? _design.Create(_gains) : EqualizerCurve.Flat;

    internal void SetOutputLevel(float level) => _requestedLevel = level;

    internal void Reposition(Action seek)
    {
        lock (_decoderGate)
        {
            seek();
            _resetPending = true;
        }
    }

    public int Read(Span<float> buffer)
    {
        int count;
        bool reset;
        lock (_decoderGate)
        {
            count = _source.Read(buffer);
            if (count == 0)
                return 0;
            reset = _resetPending;
            _resetPending = false;
        }

        EqualizerCurve requested = _requested;
        double level = _requestedLevel;
        if (reset)
        {
            _channel = _fadePosition = _levelFrames = 0;
            _current.Start(requested);
            _targetLevel = _level = level;
        }

        bool overload = false;
        for (int i = 0; i < count; i++)
        {
            if (_channel == 0)
            {
                // A mid-fade update waits for the current pair to finish. Starting
                // a third bank or interrupting the blend would introduce a step.
                if (_fadePosition == 0 && !ReferenceEquals(requested, _current.Curve))
                {
                    _next.TransitionFrom(_current, requested);
                    _fadePosition = 1;
                }
                if (level != _targetLevel)
                {
                    _targetLevel = level;
                    _levelFrames = _fadeFrames;
                    _levelStep = (level - _level) / _levelFrames;
                }
                if (_levelFrames > 0)
                    _level = --_levelFrames == 0 ? _targetLevel : _level + _levelStep;
            }

            double input = float.IsFinite(buffer[i]) ? buffer[i] : 0;
            double value = _current.Process(input, _channel);
            if (_fadePosition != 0)
                value += (_next.Process(input, _channel) - value) * (_fadePosition / (double)_fadeFrames);
            buffer[i] = (float)(value * _level);
            overload |= Math.Abs(buffer[i]) > 1;

            if (++_channel == _channels)
            {
                _channel = 0;
                if (_fadePosition != 0 && ++_fadePosition > _fadeFrames)
                {
                    (_current, _next) = (_next, _current);
                    _fadePosition = 0;
                }
            }
        }
        if (overload)
            Volatile.Write(ref _overloaded, 1);
        return count;
    }
}
