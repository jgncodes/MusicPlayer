using System;
using System.Linq;
using System.Numerics;
using Xunit.Abstractions;
using static MusicPlayer.ParametricFilterType;

namespace MusicPlayer.Tests;

public class ParametricRenderTests(ITestOutputHelper log)
{
    private const int Rate = 48000;
    private static readonly ParametricEqualizerSettings Mixed = new(new(LowShelf, 135, 5),
        new(Peaking, 1200, -8, 3), new(HighShelf, 4500, 9, 0.5));

    private static EqualizerSampleProvider Provider(float[] input, int channels, ParametricEqualizerSettings settings) =>
        new(new ArraySampleProvider(input, Rate, channels)) { ParametricSettings = settings, Mode = EqualizerMode.Parametric };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    public void PartialReadsMatchIndependentMonoHistoryForEveryChannel(int channels)
    {
        const int frames = 4096;
        float[] input = new float[frames * channels], expected = new float[input.Length];
        for (int ch = 0; ch < channels; ch++)
        {
            float[] mono = new float[frames];
            mono[ch * 97] = 0.99f;
            for (int i = 0; i < frames; i++)
                input[i * channels + ch] = mono[i];
            Assert.Equal(frames, Provider(mono, 1, Mixed).Read(mono));
            for (int i = 0; i < frames; i++)
                expected[i * channels + ch] = mono[i];
        }
        var eq = Provider(input, channels, Mixed);
        float[] output = new float[input.Length + 7];
        Array.Fill(output, 123f);
        int pos = 0;
        while (pos < input.Length)
        {
            Assert.Equal(0, eq.Read(Span<float>.Empty));
            int length = Math.Min(17, output.Length - pos);
            int read = eq.Read(output.AsSpan(pos, length));
            Assert.Equal(Math.Min(length, input.Length - pos), read);
            pos += read;
        }
        Assert.Equal(0, eq.Read(output.AsSpan(pos)));
        Assert.Equal(expected, output[..pos]);
        Assert.All(output[pos..], x => Assert.Equal(123f, x));
    }

    [Theory]
    [InlineData("Graphic", "Mixed")]
    [InlineData("Mixed", "Graphic")]
    [InlineData("Mixed", "Bypass")]
    [InlineData("Bypass", "Mixed")]
    [InlineData("Mixed", "Edited")]
    public void LiveEditBlendsExactlyTwoStableOutputsAcrossSplitStereoFrames(string from, string to)
    {
        const int channels = 2, fade = 960, start = 512;
        float[] input = TestAudioFactory.GenerateNoise(durationSeconds: 0.15, sampleRate: Rate);
        var changing = Provider(input, channels, Mixed);
        Configure(changing, from);
        float[] old = Reference(input, from);
        float[] target = Reference(input[start..], to);
        float[] actual = new float[input.Length];
        // Request arrives after the left sample. The right channel still uses the old curve.
        Assert.Equal(start - 1, changing.Read(actual.AsSpan(0, start - 1)));
        Configure(changing, to);
        for (int i = start - 1; i < actual.Length; i += 19)
            changing.Read(actual.AsSpan(i, Math.Min(19, actual.Length - i)));
        Assert.Equal(old[..start], actual[..start]);
        for (int i = start; i < actual.Length; i++)
        {
            double mix = Math.Min(1, ((i - start) / 2 + 1) / (double)fade);
            double expected = old[i] * (1 - mix) + target[i - start] * mix;
            Assert.InRange(Math.Abs(expected - actual[i]), 0, 1e-7);
        }
    }

    private static float[] Reference(float[] input, string mode)
    {
        var eq = Provider(input, 2, Mixed);
        Configure(eq, mode);
        float[] result = new float[input.Length];
        eq.Read(result);
        return result;
    }

    private static void Configure(EqualizerSampleProvider eq, string mode)
    {
        eq.SetEnabled(mode != "Bypass");
        eq.Mode = mode == "Graphic" ? EqualizerMode.Graphic : EqualizerMode.Parametric;
        eq.SetBandGain(5, -10);
        eq.ParametricSettings = mode == "Edited"
            ? new(new(Peaking, 380, -2, 9), new(HighShelf, 9300, 4, Enabled: false)) : Mixed;
    }

    [Fact]
    public void RapidRetuningFinishesCurrentFadeThenUsesOnlyTheLatestRequest()
    {
        const int fade = 960;
        float[] dc = Enumerable.Repeat(0.5f, fade * 5).ToArray();
        // At DC, a bell has unity filter gain. The automatic headroom gives
        // a large known level change, exposing a restarted/interrupted fade.
        var eq = Provider(dc, 1, ParametricEqualizerSettings.Flat);
        float[] output = new float[dc.Length];
        eq.Read(output.AsSpan(0, fade));
        var boost = new ParametricEqualizerSettings(new ParametricBand(Peaking, 6000, 18, 2));
        eq.ParametricSettings = boost;
        eq.Read(output.AsSpan(fade, 61));
        for (int i = fade + 61; i < 2 * fade; i += 7)
        {
            eq.ParametricSettings = new(new ParametricBand(LowShelf, 80 + i, -7));
            eq.SetEnabled(i % 2 == 0);
            eq.Read(output.AsSpan(i, Math.Min(7, 2 * fade - i)));
        }
        eq.SetEnabled(false);
        eq.Read(output.AsSpan(2 * fade));
        Assert.Equal(dc[^fade..], output[^fade..]);
        double attenuated = 0.5 * Math.Pow(10, -18.0 / 20);
        Assert.Equal(attenuated, output[2 * fade - 1], 6);
        Assert.Equal(attenuated + (0.5 - attenuated) / fade, output[2 * fade], 6);
        double step = 0;
        for (int i = fade; i < output.Length; i++)
            step = Math.Max(step, Math.Abs(output[i] - output[i - 1]));
        log.WriteLine($"Rapid edits, 0.5 DC: largest adjacent step {step:G8}");
        Assert.InRange(step, 0, 0.001);
    }

    [Fact]
    public void FullBankRetainsSlowBassDecayWhenOnlyTheLastBandChanges()
    {
        const int count = ParametricEqualizerSettings.MaxBands, edit = 80, fade = 960;
        // Reserve the last slot for a shelf introduced downstream of 15 ringing
        // bells. A bank restarted from silence would lose the entire impulse tail.
        ParametricBand[] bands = Enumerable.Range(0, count)
            .Select(i => new ParametricBand(Peaking, 22 + 3 * i, -8, 16)).ToArray();
        bands[^1] = new(HighShelf, 2000, 0);
        float[] signal = new float[4000];
        signal[0] = 0.99f;
        var live = Provider(signal, 1, new(bands));
        float[] upstream = new float[signal.Length];
        Provider(signal, 1, new(bands)).Read(upstream);
        float[] actual = new float[signal.Length];
        live.Read(actual.AsSpan(0, edit));
        bands[^1] = new(HighShelf, 2000, -12);
        live.ParametricSettings = new(bands);
        live.Read(actual.AsSpan(edit));
        float[] downstream = upstream[edit..];
        Provider(downstream, 1, new(bands[^1])).Read(downstream);
        for (int i = edit; i < actual.Length; i++)
        {
            double mix = Math.Min(1, (i - edit + 1) / (double)fade);
            Assert.InRange(Math.Abs(actual[i] - (upstream[i] * (1 - mix) + downstream[i - edit] * mix)), 0, 1e-8);
        }
    }

    [Fact]
    public void SettledMultitoneAmplitudesFollowTheOutputResponse()
    {
        int[] tones = [125, 1200, 6000];
        float[] input = new float[Rate * 2];
        for (int i = 0; i < input.Length; i++)
            input[i] = (float)tones.Sum(hz => 0.33 * Math.Sin(2 * Math.PI * hz * i / Rate));
        var eq = Provider(input, 1, Mixed);
        eq.SetOutputLevel(0.75f);
        var response = eq.GetResponse();
        eq.Read(input);
        foreach (int hz in tones)
        {
            // One second of settling followed by an integer number of cycles.
            Complex projection = Complex.Zero;
            for (int n = Rate; n < input.Length; n++)
                projection += input[n] * Complex.FromPolarCoordinates(1, -2 * Math.PI * hz * n / Rate);
            double db = 20 * Math.Log10(2 * projection.Magnitude / (Rate * 0.33));
            Assert.Equal(response.GetDecibels(hz, EqualizerResponseStage.Output), db, 5);
        }
    }

    [Fact]
    public void BoundedTransientCanOverloadAndFloatOverflowDoesNotPoisonState()
    {
        var settings = new ParametricEqualizerSettings(new ParametricBand(Peaking, 3000, -18, 1));
        float[] impulse = new float[1024];
        impulse[0] = 1;
        Provider(impulse, 1, settings).Read(impulse);
        float[] adversarial = impulse.Reverse().Select(x => 0.99f * Math.Sign(x)).ToArray();
        var eq = Provider(adversarial, 1, settings);
        eq.Read(adversarial);
        Assert.True(adversarial[^1] > 1);
        Assert.True(eq.Overloaded);
        eq.ClearOverload();
        Assert.False(eq.Overloaded);

        float[] extremes = impulse.Reverse().Select(x => float.MaxValue * Math.Sign(x)).Concat(
            new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity }).Concat(new float[8192]).ToArray();
        eq = Provider(extremes, 1, settings);
        eq.Read(extremes);
        Assert.All(extremes, x => Assert.True(float.IsFinite(x)));
        Assert.Equal(float.MaxValue, extremes[impulse.Length - 1]);
        Assert.Equal(0, extremes[^1]);
    }
}
