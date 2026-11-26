using System;
using System.Diagnostics;
using System.Linq;
using NAudio.Wave;
using Xunit.Abstractions;

namespace MusicPlayer.Tests;

// Run these in Release without Coverlet when comparing throughput.
public class DspMeasurementTests(ITestOutputHelper log)
{
    [Fact]
    public void MeasureGainOverlapAndControlStep()
    {
        const int rate = 48000;
        float[] tone = TestAudioFactory.GenerateSineWave(1000, 0.99, rate);
        var eq = new EqualizerSampleProvider(new ArraySampleProvider(tone, rate, 1));
        for (int band = 0; band < 10; band++)
            eq.SetBandGain(band, 12);
        float[] output = new float[tone.Length];
        Assert.Equal(output.Length, eq.Read(output));
        double peak = output.Skip(rate / 2).Max(x => Math.Abs(x));
        double peakIncludingStartup = output.Max(x => Math.Abs(x));
        log.WriteLine($"All +12 dB, 0.99-amplitude 1 kHz: settled peak {peak:F8}, {20 * Math.Log10(peak):F4} dBFS");

        float[] dc = Enumerable.Repeat(0.5f, 2 * rate).ToArray();
        eq = new EqualizerSampleProvider(new ArraySampleProvider(dc, rate, 1));
        Assert.Equal(rate, eq.Read(output));
        float before = output[^1];
        eq.SetBandGain(5, 12);
        Assert.Equal(rate, eq.Read(output));
        double step = Math.Abs(output[0] - before);
        double maxStep = step;
        for (int i = 1; i < rate; i++)
            maxStep = Math.Max(maxStep, Math.Abs(output[i] - output[i - 1]));
        log.WriteLine($"0.5 DC, 1 kHz band 0 to +12 dB: boundary step {step:G8}, maximum step {maxStep:G8}");
        float settledDc = output[^1];

        eq = new EqualizerSampleProvider(new ArraySampleProvider(dc, 16000, 1));
        eq.SetBandGain(9, 12);
        Assert.Equal(rate, eq.Read(output));
        log.WriteLine($"Inactive 16 kHz band +12 dB at 16 kHz sample rate: DC gain {20 * Math.Log10(output[^1] / 0.5):F4} dB");
        // Emit all measurements before enforcing the modernized DSP targets.
        // The same probe can then characterize an earlier checkout.
        Assert.InRange(peak, 0.90, 0.99);
        Assert.InRange(peakIncludingStartup, peak, 1.0);
        Assert.InRange(maxStep, 0, 0.001);
        Assert.InRange(settledDc, 0.125f, 0.126f);
        Assert.All(output, value => Assert.Equal(0.5f, value));
    }

    [Theory]
    [InlineData(44100, 1, 64, false)]
    [InlineData(48000, 2, 256, false)]
    [InlineData(96000, 2, 1024, false)]
    [InlineData(48000, 2, 256, true)]
    public void MeasureRenderCost(int rate, int channels, int frames, bool update)
    {
        const int calls = 512;
        float[] buffer = new float[channels * frames];
        float[] input = TestAudioFactory.GenerateNoise(sampleRate: rate,
            durationSeconds: (calls + 128) * buffer.Length / (double)rate);
        var eq = new EqualizerSampleProvider(new ArraySampleProvider(input, rate, channels));
        for (int band = 0; band < 10; band++)
            eq.SetBandGain(band, band % 2 == 0 ? 4 : -3);
        for (int i = 0; i < 128; i++)
            eq.Read(buffer);

        long readTicks = 0, readBytes = 0, updateTicks = 0;
        for (int i = 0; i < calls; i++)
        {
            if (update)
            {
                long t = Stopwatch.GetTimestamp();
                eq.SetBandGain(3, i % 2 == 0 ? 6 : -6);
                updateTicks += Stopwatch.GetTimestamp() - t;
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            int read = eq.Read(buffer);
            readTicks += Stopwatch.GetTimestamp() - start;
            readBytes += GC.GetAllocatedBytesForCurrentThread() - bytes;
            Assert.Equal(buffer.Length, read);
        }
        double seconds = readTicks / (double)Stopwatch.Frequency;
        log.WriteLine($"{rate} Hz, {channels} ch, {frames} frames, retune {update}: " +
            $"{frames * calls / seconds / 1e6:F3} Mframes/s, {readBytes} Read bytes, " +
            $"{seconds * 1e6 / calls:F2} us/read, {updateTicks * 1e6 / Stopwatch.Frequency / calls:F2} us/settings");
        Assert.All(buffer, v => Assert.True(float.IsFinite(v)));
    }
}
