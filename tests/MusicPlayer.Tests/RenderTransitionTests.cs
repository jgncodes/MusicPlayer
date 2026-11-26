using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace MusicPlayer.Tests;

public class RenderTransitionTests
{
    [Theory]
    [InlineData(8000, 1)]
    [InlineData(44100, 2)]
    [InlineData(192000, 2)]
    public void FlatAndBypassPreserveFiniteFloatBits(int rate, int channels)
    {
        float[] input = TestAudioFactory.GenerateNoise(42, 1, rate, 0.1);
        input[0] = -0f;
        input[1] = 1;
        input[2] = -1;
        input[3] = 1e-12f;
        foreach (bool enabled in new[] { true, false })
        {
            var eq = Provider(input, rate, channels);
            if (!enabled)
            {
                eq.SetBandGain(0, 12);
                eq.SetEnabled(false);
            }
            float[] actual = new float[input.Length];
            Assert.Equal(input.Length, eq.Read(actual));
            Assert.Equal(input.Select(BitConverter.SingleToInt32Bits), actual.Select(BitConverter.SingleToInt32Bits));
        }
    }

    [Fact]
    public void BypassCrossfadeUsesOneWeightPerStereoFrameAcrossOddReads()
    {
        const int rate = 8000, frames = 160;
        float[] left = TestAudioFactory.GenerateSineWave(125, 0.3, rate, 0.2);
        float[] right = TestAudioFactory.GenerateSineWave(1777, 0.4, rate, 0.2);
        float[] input = new float[left.Length * 2];
        for (int i = 0; i < left.Length; i++)
        {
            input[2 * i] = left[i] + 0.25f * right[i];
            input[2 * i + 1] = right[i];
        }
        var fixedEq = Provider(input, rate, 2);
        var changing = Provider(input, rate, 2);
        float[] preset = [8, -2, 9, 4, 0, 6, -6, 0, 0, 0];
        fixedEq.SetBands(preset);
        changing.SetBands(preset);
        float[] wet = new float[input.Length], output = new float[input.Length];
        Assert.Equal(wet.Length, fixedEq.Read(wet));
        const int start = 801; // UI change between left and right of frame 400.
        Assert.Equal(start, changing.Read(output.AsSpan(0, start)));
        changing.SetEnabled(false);
        for (int i = start; i < output.Length; i += 7)
        {
            int n = Math.Min(7, output.Length - i);
            Assert.Equal(n, changing.Read(output.AsSpan(i, n)));
        }
        for (int i = 0; i < output.Length; i++)
        {
            double mix = Math.Clamp((i / 2 - 400) / (double)frames, 0, 1);
            Assert.InRange(Math.Abs(output[i] - (wet[i] * (1 - mix) + input[i] * mix)), 0, 1e-7);
        }
        Assert.Equal(input.AsSpan(1200).ToArray(), output.AsSpan(1200).ToArray());
    }

    [Fact]
    public void RepeatedPresetEditsFinishAtLatestCurveWithoutChannelLeakage()
    {
        const int rate = 48000;
        var left = TestAudioFactory.GenerateSineWave(1000, 0.5, rate);
        float[] stereo = new float[2 * rate];
        for (int i = 0; i < rate; i++)
            stereo[2 * i] = left[i];
        var eq = Provider(stereo, rate, 2);
        float[] output = new float[stereo.Length];
        int offset = 0;
        for (int i = 0; i < 601; i++)
        {
            eq.SetBands(i % 2 == 0 ? [5, 5, 4, 2, 0, 0, 0, 0, 0, 0] : [4, 3, 1, -1, -2, -1, 1, 3, 4, 3]);
            eq.SetEnabled(i % 3 != 0);
            eq.SetBandGain(5, i % 2 == 0 ? -12 : 12);
            Assert.Equal(61, eq.Read(output.AsSpan(offset, 61)));
            offset += 61;
        }
        eq.SetEnabled(true);
        eq.SetBands([0, 0, 0, 0, 0, -6, 0, 0, 0, 0]);
        Assert.Equal(output.Length - offset, eq.Read(output.AsSpan(offset)));
        for (int i = 0; i < output.Length; i += 2)
        {
            Assert.InRange(output[i], -1f, 1f);
            Assert.Equal(0, output[i + 1]);
        }
        var reference = Provider(stereo, rate, 2);
        reference.SetBandGain(5, -6);
        float[] steady = new float[stereo.Length];
        reference.Read(steady);
        Assert.Equal(steady[^960..], output[^960..]);
    }

    [Fact]
    public void EditingTreblePreservesBassFilterMemory()
    {
        const int rate = 48000, start = 80, fade = 960;
        float[] input = new float[1600];
        input[0] = 1;
        var bass = Provider(input, rate, 1);
        var changing = Provider(input, rate, 1);
        bass.SetBandGain(0, -12);
        changing.SetBandGain(0, -12);
        float[] low = new float[input.Length], actual = new float[input.Length];
        bass.Read(low);
        changing.Read(actual.AsSpan(0, start));
        changing.SetBandGain(8, -12);
        changing.Read(actual.AsSpan(start));
        // The treble stage starts cold, driven by the continuous bass output.
        var treble = Provider(low[start..], rate, 1);
        treble.SetBandGain(8, -12);
        float[] target = new float[input.Length - start];
        treble.Read(target);
        for (int i = 0; i < target.Length; i++)
        {
            double mix = Math.Min(1, (i + 1.0) / fade);
            double expected = (1 - mix) * low[start + i] + mix * target[i];
            Assert.InRange(Math.Abs(actual[start + i] - expected), 0, 1e-8);
        }
    }

    [Fact]
    public void ChangingAnInaudibleBandPreservesTheActiveFilterHistory()
    {
        const int rate = 8000;
        float[] input = TestAudioFactory.GenerateNoise(sampleRate: rate);
        var eq = Provider(input, rate, 1);
        var reference = Provider(input, rate, 1);
        eq.SetBandGain(0, -12);
        reference.SetBandGain(0, -12);
        float[] output = new float[rate], expected = new float[rate];
        reference.Read(expected);
        eq.Read(output.AsSpan(0, 1001));
        eq.SetBandGain(9, 12);
        eq.SetBands([-12, 0, 0, 0, 0, 0, 0, 6, 0, 12]);
        eq.Read(output.AsSpan(1001));
        Assert.Equal(expected, output);
    }

    [Fact]
    public void NonfiniteFileSamplesCannotPoisonFilterHistory()
    {
        float[] input = [0.5f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0, 1];
        float[] clean = [0.5f, 0, 0, 0, 0, 1];
        var eq = Provider(input, 48000, 1);
        var reference = Provider(clean, 48000, 1);
        eq.SetBandGain(4, -8);
        reference.SetBandGain(4, -8);
        eq.Read(input);
        reference.Read(clean);
        Assert.Equal(clean, input);
    }

    [Theory]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NaN)]
    public void InvalidSettingsPreservePreviousCurveVolumeAndTrim(float invalid)
    {
        var player = new AudioPlaybackService { Volume = 0.6, OutputTrimDb = -4 };
        var eq = Provider([0.5f], 48000, 1);
        player.SetBandGain(0, 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => player.Volume = invalid);
        Assert.Throws<ArgumentOutOfRangeException>(() => player.OutputTrimDb = invalid);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.SetBandGain(0, invalid));
        float[] gains = new float[10];
        gains[^1] = invalid;
        Assert.Throws<ArgumentOutOfRangeException>(() => player.SetPreset(gains));
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.SetBands(gains));
        Assert.Throws<ArgumentException>(() => player.SetPreset([1]));
        Assert.Equal(6f, player.GetBandGain(0));
        Assert.Equal(-4f, player.OutputTrimDb);
        Assert.Equal(0.6, player.Volume);
        player.OutputTrimDb = -99;
        Assert.Equal(-24f, player.OutputTrimDb);
        player.OutputTrimDb = 14;
        Assert.Equal(0f, player.OutputTrimDb);
    }

    [Fact]
    public async Task SettingsCanBePublishedWhileDecoderIsBlocked()
    {
        using var allowRead = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eq = new EqualizerSampleProvider(new BlockedSource(entered, allowRead));
        float[] buffer = new float[8000];
        Task<int> reading = Task.Run(() => eq.Read(buffer));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Run(() => eq.SetBands([0, 0, 0, 0, 0, 12, 0, 0, 0, 0]))
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { allowRead.Set(); }
        Assert.Equal(buffer.Length, await reading);
        Assert.Equal(0.5 * Math.Pow(10, -12.0 / 20), buffer[^1], 6);
    }

    private sealed class BlockedSource(TaskCompletionSource entered, ManualResetEventSlim allowRead) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);
        public int Read(Span<float> buffer)
        {
            entered.TrySetResult();
            allowRead.Wait();
            buffer.Fill(0.5f);
            return buffer.Length;
        }
    }

    private static EqualizerSampleProvider Provider(float[] input, int rate, int channels) =>
        new(new ArraySampleProvider(input, rate, channels));
}
