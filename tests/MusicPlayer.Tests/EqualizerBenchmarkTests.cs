using System;
using System.Diagnostics;
using Xunit.Abstractions;

namespace MusicPlayer.Tests;

public class EqualizerBenchmarkTests(ITestOutputHelper log)
{
    [Theory]
    [InlineData(44100, 1, 64)]
    [InlineData(44100, 2, 256)]
    [InlineData(48000, 1, 256)]
    [InlineData(48000, 2, 64)]
    [InlineData(48000, 2, 256)]
    [InlineData(96000, 2, 256)]
    public void MeasureProvider(int rate, int channels, int frames)
    {
        const int warmup = 128, calls = 513;
        float[] buffer = new float[frames * channels];
        float[] signal = TestAudioFactory.GenerateNoise(durationSeconds:
            (warmup + calls + 1) * buffer.Length / (double)rate, sampleRate: rate);
        var mixed = new ParametricEqualizerSettings(new(ParametricFilterType.LowShelf, 140, 5),
            new(ParametricFilterType.Peaking, 450, -4, 2), new(ParametricFilterType.Peaking, 890, -3, 3),
            new(ParametricFilterType.Peaking, 3100, 6, 4), new(ParametricFilterType.HighShelf, 8400, -3));
        var alternative = new ParametricEqualizerSettings([mixed.Bands[0] with { GainDb = -6 },
            mixed.Bands[1], mixed.Bands[2], mixed.Bands[3], mixed.Bands[4]]);
        foreach (string mode in new[] { "Graphic10", "Bypass", "Peak3", "Mixed5", "Mixed5Live" })
        {
            var eq = new EqualizerSampleProvider(new ArraySampleProvider(signal, rate, channels));
            for (int i = 0; i < 10; i++)
                eq.SetBandGain(i, i % 2 == 0 ? 4 : -3);
            eq.SetEnabled(mode != "Bypass");
            if (mode is "Peak3" or "Mixed5" or "Mixed5Live")
            {
                eq.ParametricSettings = mode == "Peak3"
                    ? new(mixed.Bands[1], mixed.Bands[2], mixed.Bands[3]) : mixed;
                eq.Mode = EqualizerMode.Parametric;
            }
            for (int i = 0; i < warmup; i++)
                eq.Read(buffer);

            long[] ticks = new long[calls];
            long bytes = 0;
            int updates = 0;
            long updateTicks = 0;
            for (int i = 0; i < calls; i++)
            {
                if (mode == "Mixed5Live" && (long)i * frames * 60 >= (long)updates * rate)
                {
                    long startUpdate = Stopwatch.GetTimestamp();
                    eq.ParametricSettings = updates % 2 == 0 ? alternative : mixed;
                    updateTicks += Stopwatch.GetTimestamp() - startUpdate;
                    updates++;
                }
                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                int read = eq.Read(buffer);
                ticks[i] = Stopwatch.GetTimestamp() - start;
                bytes += GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Equal(buffer.Length, read);
            }
            Array.Sort(ticks);
            double us = ticks[calls / 2] * 1e6 / Stopwatch.Frequency;
            log.WriteLine($"{rate} Hz {channels}ch {frames} frames {mode}: {us:F2} us, " +
                $"{frames * 1e6 / (rate * us):F0}x real time, {bytes / (double)calls:F1} B/read");
            if (updates > 0)
                log.WriteLine($"Control thread: {updates} updates, {updateTicks * 1e6 / Stopwatch.Frequency / updates:F2} us/update mean");
        }
    }
}
