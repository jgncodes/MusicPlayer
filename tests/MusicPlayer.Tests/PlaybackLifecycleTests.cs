using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using Xunit;

namespace MusicPlayer.Tests;

public class PlaybackLifecycleTests
{
    [Fact]
    public void OpenSeekMuteAndLiveGainReachTheDecodedStream()
    {
        using var wav = new TemporaryWavFile(TestAudioFactory.GenerateSineWave(1000, 0.5, 8000), 8000);
        var output = new RecordingOutput();
        var player = new AudioPlaybackService(() => output, action => action());
        int opened = 0;
        player.MediaOpened += (_, _) => opened++;
        player.Volume = 0.5;
        player.SetBandGain(5, -12);
        player.SetEqualizerEnabled(false);

        try
        {
            player.Open(new Uri(wav.FilePath));
            Assert.Equal(1, opened);
            Assert.Equal(TimeSpan.FromSeconds(1), player.Duration);
            player.Position = TimeSpan.FromMilliseconds(250);
            Assert.Equal(TimeSpan.FromMilliseconds(250), player.Position);
            var samples = output.Source.ToSampleProvider();
            Assert.InRange(ReadPeak(samples), 0.249f, 0.251f);
            player.IsMuted = true;
            Assert.Equal(0, ReadPeak(samples));
            player.IsMuted = false;
            player.SetEqualizerEnabled(true);
            Assert.InRange(ReadPeak(samples), 0.060f, 0.066f);
            player.ResetEqualizer();
            Assert.InRange(ReadPeak(samples), 0.249f, 0.251f);

            player.Play();
            Assert.Equal(PlaybackState.Playing, output.PlaybackState);
            player.Pause();
            Assert.Equal(PlaybackState.Paused, output.PlaybackState);
            player.Stop();
            Assert.Equal(PlaybackState.Stopped, output.PlaybackState);
        }
        finally { player.Close(); }
        Assert.True(output.Disposed);
        Assert.Null(player.Duration);
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    private static float ReadPeak(ISampleProvider source)
    {
        float[] buffer = new float[512];
        Assert.Equal(buffer.Length, source.Read(buffer));
        float peak = 0;
        foreach (float value in buffer.AsSpan(256))
            peak = Math.Max(peak, Math.Abs(value));
        return peak;
    }

    [Fact]
    public void OnlyCurrentTrackRaisesEndOrErrorAfterDispatch()
    {
        using var wav = new TemporaryWavFile(new float[40]);
        var first = new RecordingOutput();
        var second = new RecordingOutput();
        var third = new RecordingOutput();
        var devices = new Queue<RecordingOutput>([first, second, third]);
        var callbacks = new Queue<Action>();
        var player = new AudioPlaybackService(() => devices.Dequeue(), callbacks.Enqueue);
        var errors = new List<Exception>();
        int ended = 0;
        player.MediaEnded += (_, _) => ended++;
        player.MediaFailed += (_, e) => errors.Add(e.ErrorException);

        try
        {
            player.Open(new Uri(wav.FilePath));
            first.NotifyStopped();
            callbacks.Dequeue()();
            Assert.Equal(0, ended);

            player.Position = player.Duration!.Value;
            first.NotifyStopped();
            player.Play();
            player.Open(new Uri(wav.FilePath));
            Assert.False(first.Disposed);
            first.NotifyStopped();
            Assert.True(first.Disposed);
            callbacks.Dequeue()();
            Assert.Equal(0, ended);

            var error = new IOException("driver stopped");
            second.NotifyStopped(error);
            callbacks.Dequeue()();
            Assert.Same(error, Assert.Single(errors));
            Assert.Equal(0, ended);
            Assert.True(second.Disposed);
            Assert.Null(player.Duration);
            player.Open(new Uri(wav.FilePath));
            byte[] buffer = new byte[256];
            Assert.True(third.Source.Read(buffer) > 0);
            Assert.Equal(0, third.Source.Read(buffer));
            third.NotifyStopped();
            Assert.Equal(0, ended);
            callbacks.Dequeue()();
            Assert.Equal(1, ended);
        }
        finally { player.Close(); }
        Assert.True(third.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOutputReleasesReaderAndAllowsRetry(bool failConstruction)
    {
        using var wav = new TemporaryWavFile(new float[32]);
        var error = new InvalidOperationException("device unavailable");
        var broken = new RecordingOutput { InitError = error };
        var healthy = new RecordingOutput();
        int attempts = 0;
        var player = new AudioPlaybackService(() =>
        {
            if (++attempts == 1)
                return failConstruction ? throw error : broken;
            return healthy;
        }, action => action());
        Exception? failure = null;
        player.MediaFailed += (_, e) => failure = e.ErrorException;
        try
        {
            player.Open(new Uri(wav.FilePath));
            Assert.Same(error, failure);
            Assert.Null(player.Duration);
            Assert.Equal(!failConstruction, broken.Disposed);
            using (File.Open(wav.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            player.Open(new Uri(wav.FilePath));
            Assert.NotNull(player.Duration);
        }
        finally { player.Close(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrMalformedWavRaisesFailureBeforeCreatingOutput(bool missing)
    {
        using var wav = new TemporaryWavFile([]);
        if (missing)
            File.Delete(wav.FilePath);
        else
            File.WriteAllText(wav.FilePath, "invalid WAV");
        int devices = 0, opened = 0;
        var player = new AudioPlaybackService(() => { devices++; return new RecordingOutput(); }, a => a());
        var failures = new List<Exception>();
        player.MediaFailed += (_, e) => failures.Add(e.ErrorException);
        player.MediaOpened += (_, _) => opened++;
        player.Open(new Uri(wav.FilePath));
        Assert.Single(failures);
        Assert.Equal(0, opened);
        Assert.Equal(0, devices);
        Assert.Null(player.Duration);
    }
}
