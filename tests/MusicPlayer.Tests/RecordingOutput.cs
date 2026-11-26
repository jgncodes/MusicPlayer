using System;
using NAudio.Wave;

namespace MusicPlayer.Tests;

// Controls exactly when the driver's stop notification arrives.
internal sealed class RecordingOutput : IWavePlayer
{
    internal IWaveProvider Source { get; private set; } = null!;
    internal bool Disposed { get; private set; }
    internal Exception? InitError { get; init; }
    public PlaybackState PlaybackState { get; private set; }
    public WaveFormat OutputWaveFormat => Source.WaveFormat;
    public float Volume { get; set; }
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public void Init(IWaveProvider provider)
    {
        if (InitError != null)
            throw InitError;
        Source = provider;
    }

    public void Play() => PlaybackState = PlaybackState.Playing;
    public void Pause() => PlaybackState = PlaybackState.Paused;
    internal bool CompleteStopImmediately { get; init; }
    public void Stop()
    {
        if (PlaybackState == PlaybackState.Stopped)
            return;
        PlaybackState = PlaybackState.Stopped;
        if (CompleteStopImmediately)
            NotifyStopped();
    }
    public void Dispose() => Disposed = true;

    internal void NotifyStopped(Exception? error = null)
    {
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
    }
}
