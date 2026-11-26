using System;
using NAudio.Wave;

namespace MusicPlayer.Tests;

// Controls exactly when the driver's stop notification arrives.
internal sealed class RecordingOutput : IWavePlayer
{
    internal IWaveProvider Source { get; private set; } = null!;
    internal bool Disposed => DisposeCount > 0;
    internal int DisposeCount { get; private set; }
    internal Exception? InitError { get; init; }
    internal Action<IWaveProvider>? OnInit { get; init; }
    internal string? FailingCommand { get; set; }
    public PlaybackState PlaybackState { get; private set; }
    public WaveFormat OutputWaveFormat => Source.WaveFormat;
    public float Volume { get; set; }
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public void Init(IWaveProvider provider)
    {
        if (InitError != null)
            throw InitError;
        Source = provider;
        OnInit?.Invoke(provider);
    }

    public void Play() { CheckCommand(nameof(Play)); PlaybackState = PlaybackState.Playing; }
    public void Pause() { CheckCommand(nameof(Pause)); PlaybackState = PlaybackState.Paused; }
    internal bool CompleteStopImmediately { get; init; }
    public void Stop()
    {
        CheckCommand(nameof(Stop));
        if (PlaybackState == PlaybackState.Stopped)
            return;
        PlaybackState = PlaybackState.Stopped;
        if (CompleteStopImmediately)
            NotifyStopped();
    }
    public void Dispose() => DisposeCount++;

    private void CheckCommand(string command)
    {
        if (FailingCommand == command)
            throw new InvalidOperationException(command);
    }

    internal void NotifyStopped(Exception? error = null)
    {
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
    }
}
