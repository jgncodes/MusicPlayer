using System;
using System.Diagnostics;
using System.Linq;
using Xunit.Abstractions;
using static MusicPlayer.ParametricFilterType;

namespace MusicPlayer.Tests;

public class ResponsePlotTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(8000, 200, 80)]
    [InlineData(44100, 680, 240)]
    [InlineData(192000, 1200, 600)]
    public void LogFrequencyAndLinearGainRoundTripWithClampedEndpoints(int rate, double w, double h)
    {
        double low = ParametricEqualizerSettings.MinimumHz(rate), high = ParametricEqualizerSettings.MaximumHz(rate);
        var scale = new PlotScale(low, high, -48, 24, w, h);
        Assert.Equal(w / 2, scale.X(Math.Sqrt(low * high)), 8);
        Assert.Equal(Math.Sqrt(low * high), scale.Hz(w / 2), 8);
        Assert.Equal(0, scale.X(-10)); Assert.Equal(w, scale.X(1e9), 8);
        Assert.Equal(low, scale.Hz(-100)); Assert.Equal(high, scale.Hz(w + 100), 8);
        Assert.Equal(-12, scale.Db(h / 2)); Assert.Equal(h / 2, scale.Y(-12));
        Assert.Equal(24, scale.Db(-10)); Assert.Equal(-48, scale.Db(h + 1));
        Assert.Equal(h, scale.Y(-80)); Assert.Equal(0, scale.Y(30));
    }

    [Theory]
    [InlineData(0, 60)] [InlineData(80, -1)] [InlineData(double.NaN, 80)] [InlineData(200, double.PositiveInfinity)]
    public void RejectsInvalidDimensions(double w, double h) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlotScale(5, 20000, -24, 24, w, h));

    [Fact]
    public void SamplesMatchEveryDspStageAndIndividualBandIncludingNarrowOverlaps()
    {
        var player = new AudioPlaybackService { Mode = EqualizerMode.Parametric, Volume = 0.5, OutputTrimDb = -9 };
        using var editor = new EqualizerEditor(player);
        foreach (var band in new ParametricBand[] { new(Peaking, 1013.47, 24, 16), new(Peaking, 1013.47, -19, 16),
            new(LowShelf, 280, 9), new(HighShelf, 8000, -12) })
        {
            player.ParametricSettings = new(band);
            var response = editor.Response();
            var plot = ResponsePlot.Sample(response, editor.Bands, 0, ResponseOverlay.SelectedBand, 256);
            Assert.Contains(band.FrequencyHz, plot.Frequencies);
            int center = Array.IndexOf(plot.Frequencies, band.FrequencyHz);
            Assert.Equal(band.Type == Peaking ? band.GainDb : band.GainDb / 2, plot.Filters[center], 7);
            Assert.Equal(plot.Filters[center], plot.Overlay![center], 8);
        }
        player.ParametricSettings = new(new ParametricBand(Peaking, 1013.47, 24, 16), new(Peaking, 1013.47, 24, 16));
        AssertStages();
        player.IsMuted = true;
        AssertStages();
        player.SetEqualizerEnabled(false);
        var flat = ResponsePlot.Sample(editor.Response(), editor.Bands, -1, ResponseOverlay.None, 300);
        Assert.All(flat.Filters, db => Assert.Equal(0, db));
        Assert.Null(flat.Overlay);
        Assert.Equal(-24, flat.LowDb); Assert.Equal(24, flat.HighDb);

        void AssertStages()
        {
            var response = editor.Response();
            foreach (var (overlay, stage) in new[] { (ResponseOverlay.Output, EqualizerResponseStage.Output),
                (ResponseOverlay.Headroom, EqualizerResponseStage.WithHeadroom) })
            {
                var plot = ResponsePlot.Sample(response, editor.Bands, 0, overlay, 512);
                Assert.Equal(48, plot.Filters.Max(), 7);
                Assert.InRange(plot.Filters.Max(), 47.99, plot.HighDb + 1e-8);
                bool silent = player.IsMuted && overlay == ResponseOverlay.Output;
                Assert.Equal(silent, plot.Silent);
                Assert.Equal(silent, plot.Overlay == null);
                for (int i = 0; i < plot.Frequencies.Length; i++)
                {
                    double hz = plot.Frequencies[i];
                    Assert.Equal(response.GetDecibels(hz), plot.Filters[i], 8);
                    if (!silent)
                    {
                        Assert.Equal(response.GetDecibels(hz, stage), plot.Overlay![i], 8);
                        Assert.InRange(plot.Overlay[i], plot.LowDb - 1e-8, plot.HighDb + 1e-8);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(3, 256)] [InlineData(5, 512)] [InlineData(16, 512)]
    public void MeasureResponseSampling(int count, int samples)
    {
        var bands = Enumerable.Range(0, count).Select(i =>
            new ParametricBand(Peaking, 35 * Math.Pow(1.46, i), i % 2 == 0 ? 9 : -7, 16)).ToArray();
        var response = new ParametricEqualizerSettings(bands).CreateResponse(48000);
        ResponsePlot Sample() => ResponsePlot.Sample(response, bands, 0, ResponseOverlay.SelectedBand, samples);
        for (int i = 0; i < 12; i++) Sample();
        var times = new double[101];
        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < times.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            Sample();
            times[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - startBytes) / times.Length;
        Array.Sort(times);
        output.WriteLine($"{count} bands / {samples} base samples: median {times[50]:F1} µs, p95 {times[95]:F1} µs, {bytes} B/sample pass");
    }
}
