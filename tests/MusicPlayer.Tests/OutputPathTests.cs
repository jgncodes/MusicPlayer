using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using Xunit.Abstractions;

namespace MusicPlayer.Tests;

public class OutputPathTests(ITestOutputHelper log)
{
    [Fact]
    public void MutedOpenIsSilentEvenIfOutputPrefillsDuringInit()
    {
        using var wav = new TemporaryWavFile([0.75f, 1f]);
        float[] initial = new float[2];
        var device = new RecordingOutput { OnInit = source => source.ToSampleProvider().Read(initial) };
        var player = Player(device);
        player.IsMuted = true;
        try
        {
            player.Open(new Uri(wav.FilePath));
            Assert.NotNull(player.Duration);
            Assert.Equal(player.Duration, player.Position);
            Assert.Equal(new float[2], initial);
        }
        finally { player.Close(); }
    }

    [Theory]
    [InlineData(8, 8000, 1)]
    [InlineData(16, 44100, 2)]
    [InlineData(24, 48000, 2)]
    [InlineData(32, 96000, 1)]
    public void PcmWavReachesOutputAsNativeRateInterleavedFloat(int bits, int rate, int channels)
    {
        using var dir = new TestDirectory();
        string path = Path.Combine(dir.Path, "source.wav");
        float[] expected = [0, -0.25f, 0.5f, -1, -0.75f, 0.25f];
        using (var wav = new WaveFileWriter(path, new WaveFormat(rate, bits, channels)))
        {
            foreach (float x in expected)
            {
                long sample = bits == 8 ? (long)(128 + 128 * x) : (long)(x * (1L << (bits - 1)));
                for (int b = 0; b < bits / 8; b++)
                    wav.WriteByte((byte)(sample >> (8 * b)));
            }
        }
        var device = new RecordingOutput();
        var player = Player(device);
        try
        {
            player.Open(new Uri(path));
            Assert.Equal(WaveFormatEncoding.IeeeFloat, device.Source.WaveFormat.Encoding);
            Assert.Equal(32, device.Source.WaveFormat.BitsPerSample);
            Assert.Equal(rate, device.Source.WaveFormat.SampleRate);
            Assert.Equal(channels, device.Source.WaveFormat.Channels);
            float[] actual = new float[expected.Length];
            Assert.Equal(expected.Length, device.Source.ToSampleProvider().Read(actual));
            Assert.Equal(expected, actual);
        }
        finally { player.Close(); }
    }

    [Fact]
    public void OutputTrimVolumeAndMuteRampAcrossBuffersEvenInBypass()
    {
        const int rate = 8000, frames = 160;
        using var wav = new TemporaryWavFile(Enumerable.Repeat(1f, rate * 2).ToArray(), rate, 2);
        var device = new RecordingOutput();
        var player = Player(device);
        player.Volume = 0.8;
        player.SetEqualizerEnabled(false);
        try
        {
            player.Open(new Uri(wav.FilePath));
            var source = device.Source.ToSampleProvider();
            float[] buffer = new float[frames * 2];
            Assert.Equal(buffer.Length, source.Read(buffer));
            Assert.All(buffer, x => Assert.Equal(0.8f, x));
            player.OutputTrimDb = -6;
            foreach (bool mute in new[] { false, true, false })
            {
                player.IsMuted = mute;
                double start = buffer[^1];
                double target = mute ? 0 : 0.8 * Math.Pow(10, -6.0 / 20);
                Assert.Equal(40, source.Read(buffer.AsSpan(0, 40)));
                Assert.Equal(buffer.Length - 40, source.Read(buffer.AsSpan(40)));
                for (int f = 0; f < frames; f++)
                {
                    double expected = start + (target - start) * ((f + 1.0) / frames);
                    Assert.InRange(Math.Abs(expected - buffer[2 * f]), 0, 6e-8);
                    Assert.Equal(buffer[2 * f], buffer[2 * f + 1]);
                }
            }
            Assert.False(player.OutputOverloaded);
        }
        finally { player.Close(); }
    }

    [Fact]
    public void BoundedTransientCanOverloadAndManualTrimRestoresMargin()
    {
        const int rate = 48000;
        // Sign-reversed-in-time impulse response realizes the FIR-prefix L1 peak
        // for a bounded input. Even a cut can increase a transient sample peak.
        float[] impulse = new float[512];
        impulse[0] = 1;
        var eq = new EqualizerSampleProvider(new ArraySampleProvider(impulse, rate, 1));
        eq.SetBandGain(5, -12);
        eq.Read(impulse);
        float[] input = impulse.Reverse().Select(x => (float)Math.Sign(x)).ToArray();
        using var wav = new TemporaryWavFile(input, rate);
        var device = new RecordingOutput();
        var player = Player(device);
        player.SetBandGain(5, -12);
        try
        {
            player.Open(new Uri(wav.FilePath));
            var source = device.Source.ToSampleProvider();
            float[] output = new float[input.Length];
            source.Read(output);
            double peak = output.Max(x => Math.Abs(x));
            Assert.True(peak > 1);
            Assert.True(player.OutputOverloaded);
            player.OutputTrimDb = -12;
            player.Position = TimeSpan.Zero;
            player.ClearOverload();
            Assert.Equal(output.Length, source.Read(output));
            double trimmed = output.Max(x => Math.Abs(x));
            Assert.Equal(peak * Math.Pow(10, -12.0 / 20), trimmed, 6);
            Assert.False(player.OutputOverloaded);
            log.WriteLine($"Full-scale signed transient, 1 kHz -12 dB: peak {peak:F6}, after -12 dB trim {trimmed:F6}");
        }
        finally { player.Close(); }
    }

    [Fact]
    public void SeekClearsRingingAndInFlightFadeBeforeNewAudio()
    {
        const int rate = 48000;
        float[] input = new float[rate];
        input[100] = 1;
        using var wav = new TemporaryWavFile(input, rate);
        var device = new RecordingOutput();
        var player = Player(device);
        player.SetBandGain(0, 12);
        try
        {
            player.Open(new Uri(wav.FilePath));
            var source = device.Source.ToSampleProvider();
            float[] buffer = new float[101];
            source.Read(buffer);
            player.SetBandGain(0, -12);
            source.Read(buffer);
            Assert.Contains(buffer, x => x != 0);
            player.Position = TimeSpan.FromMilliseconds(500);
            source.Read(buffer);
            Assert.All(buffer, x => Assert.Equal(0, x));
            player.Position = TimeSpan.MaxValue;
            Assert.Equal(player.Duration, player.Position);
            Assert.Equal(0, source.Read(buffer));
            player.Position = TimeSpan.MinValue;
            Assert.Equal(TimeSpan.Zero, player.Position);
            Assert.Equal(buffer.Length, source.Read(buffer));
            Assert.InRange(buffer[^1], 0.99f, 1f);
        }
        finally { player.Close(); }
    }

    [Theory]
    [InlineData("Play")]
    [InlineData("Pause")]
    [InlineData("Stop")]
    [InlineData("Close")]
    public void DriverCommandFailureReportsOnceAndReleasesFile(string command)
    {
        using var wav = new TemporaryWavFile(new float[80]);
        var device = new RecordingOutput { CompleteStopImmediately = true };
        var player = Player(device);
        List<Exception> errors = [];
        player.MediaFailed += (_, e) => errors.Add(e.ErrorException);
        player.Open(new Uri(wav.FilePath));
        player.Play();
        device.FailingCommand = command == "Close" ? "Stop" : command;
        switch (command)
        {
            case "Play": player.Play(); break;
            case "Pause": player.Pause(); break;
            case "Stop": player.Stop(); break;
            default: player.Close(); break;
        }
        Assert.Single(errors);
        Assert.Null(player.Duration);
        device.NotifyStopped();
        player.Close();
        Assert.Equal(1, device.DisposeCount);
        using var exclusive = File.Open(wav.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void StopAfterReadAheadAndLateEndAfterSeekDoNotAdvanceTrack()
    {
        using var wav = new TemporaryWavFile(new float[80]);
        var device = new RecordingOutput();
        var queue = new Queue<Action>();
        var player = new AudioPlaybackService(() => device, queue.Enqueue);
        int ended = 0;
        player.MediaEnded += (_, _) => ended++;
        try
        {
            player.Open(new Uri(wav.FilePath));
            player.Play();
            player.Position = player.Duration!.Value;
            player.Stop();
            device.NotifyStopped();
            Assert.Empty(queue);
            player.Play();
            device.NotifyStopped();
            player.Position = TimeSpan.Zero;
            player.Position = player.Duration.Value;
            queue.Dequeue()();
            Assert.Equal(0, ended);
            device.NotifyStopped();
            queue.Dequeue()();
            Assert.Equal(1, ended);
        }
        finally { player.Close(); }
    }

    private static AudioPlaybackService Player(RecordingOutput device) => new(() => device, a => a());
}
