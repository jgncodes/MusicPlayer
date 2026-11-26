using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using static MusicPlayer.ParametricFilterType;

namespace MusicPlayer.Tests;

public class ParametricPlaybackTests
{
    [Fact]
    public void TrackRateControlsAvailableBandsAndPreferencesSurviveDriverFailure()
    {
        var settings = new ParametricEqualizerSettings(new(Peaking, 12000, 9), new(LowShelf, 180, -9));
        float[] impulse = new float[4000];
        impulse[0] = 0.9f;
        using var high = new TemporaryWavFile(impulse, 48000, 2);
        using var low = new TemporaryWavFile(impulse, 16000, 1);
        var devices = new Queue<RecordingOutput>(Enumerable.Range(0, 3).Select(_ => new RecordingOutput()));
        var player = new AudioPlaybackService(devices.Dequeue, a => a());
        var failures = new List<Exception>();
        player.MediaFailed += (_, e) => failures.Add(e.ErrorException);
        Assert.Null(player.GetEqualizerResponse());
        Assert.Equal(EqualizerMode.Graphic, player.Mode);
        player.Mode = EqualizerMode.Parametric;
        player.ParametricSettings = settings;
        player.OutputTrimDb = -6;
        player.Volume = 0.5;
        try
        {
            var first = devices.Peek();
            player.Open(new Uri(high.FilePath));
            var snapshot = player.GetEqualizerResponse()!;
            Assert.Equal(9, snapshot.GetBandDecibels(0, 12000), 7);
            Assert.Equal(snapshot.GetDecibels(12000, EqualizerResponseStage.WithHeadroom) - 6 + 20 * Math.Log10(0.5),
                snapshot.GetDecibels(12000, EqualizerResponseStage.Output), 5);
            float[] original = Read(first, 2000);
            // The WAV decoder requires whole frames. Seek during the output crossfade.
            player.SetEqualizerEnabled(false);
            Read(first, 2);
            player.SetEqualizerEnabled(true);
            player.Position = TimeSpan.Zero;
            Assert.Equal(original, Read(first, original.Length));
            player.Position = TimeSpan.FromMilliseconds(25);
            Assert.All(Read(first, 80), x => Assert.Equal(0, x));

            var second = devices.Peek();
            player.Open(new Uri(low.FilePath));
            Assert.True(first.Disposed);
            var atLowRate = player.GetEqualizerResponse()!;
            Assert.Equal(16000, atLowRate.SampleRate);
            Assert.Equal(2, atLowRate.BandCount);
            Assert.Equal(0, atLowRate.GetBandDecibels(0, 7000));
            Assert.Equal(-9, atLowRate.GetBandDecibels(1, 0), 7);
            Assert.Equal(0, atLowRate.HeadroomDb);
            Assert.Throws<ArgumentOutOfRangeException>(() => player.ParametricSettings =
                new(new ParametricBand(HighShelf, 8000, 12)));
            Assert.Same(settings, player.ParametricSettings);
            Assert.Equal(1, second.Source.WaveFormat.Channels);
            var error = new IOException("simulated output failure");
            second.NotifyStopped(error);
            Assert.Same(error, Assert.Single(failures));
            Assert.True(second.Disposed);
            Assert.Null(player.GetEqualizerResponse());
            var third = devices.Peek();
            player.Open(new Uri(high.FilePath));
            Assert.Equal(original, Read(third, original.Length));
            Assert.Equal(9, player.GetEqualizerResponse()!.GetBandDecibels(0, 12000), 7);
            player.IsMuted = true;
            Assert.Equal(double.NegativeInfinity, player.GetEqualizerResponse()!.GetDecibels(1000, EqualizerResponseStage.Output));
            player.ResetEqualizer();
            Assert.Empty(player.ParametricSettings.Bands);
            Assert.Equal(0, player.OutputTrimDb);
        }
        finally { player.Close(); }
    }

    [Fact]
    public void GraphicPresetAndParametricEditsStayIndependentAcrossBypass()
    {
        using var wav = new TemporaryWavFile(new float[1024]);
        var device = new RecordingOutput();
        var player = new AudioPlaybackService(() => device, a => a());
        player.Open(new Uri(wav.FilePath));
        try
        {
            player.SetPreset([0, 0, 0, 0, 0, -6, 0, 0, 0, 0]);
            player.ParametricSettings = new(new ParametricBand(Peaking, 1000, 9));
            Assert.Equal(-6, player.GetEqualizerResponse()!.GetDecibels(1000), 7);
            player.Mode = EqualizerMode.Parametric;
            player.SetBandGain(5, -12);
            player.SetPreset([0, 0, 0, 0, 0, -8, 0, 0, 0, 0]);
            Assert.Equal(9, player.GetEqualizerResponse()!.GetDecibels(1000), 7);
            player.SetEqualizerEnabled(false);
            player.Mode = EqualizerMode.Graphic;
            player.OutputTrimDb = -12;
            Assert.Equal(-12, player.GetEqualizerResponse()!.GetDecibels(1000, EqualizerResponseStage.Output), 5);
            player.SetEqualizerEnabled(true);
            Assert.Equal(-8, player.GetEqualizerResponse()!.GetDecibels(1000), 7);
            Assert.Equal(-8, player.GetEqualizerResponse()!.GetBandDecibels(5, 1000), 7);
            Assert.Throws<ArgumentOutOfRangeException>(() => player.Mode = (EqualizerMode)99);
            Assert.Throws<ArgumentNullException>(() => player.ParametricSettings = null!);
            Assert.Equal(EqualizerMode.Graphic, player.Mode);
        }
        finally { player.Close(); }
    }

    private static float[] Read(RecordingOutput device, int count)
    {
        float[] result = new float[count];
        Assert.Equal(count, device.Source.ToSampleProvider().Read(result));
        return result;
    }
}
