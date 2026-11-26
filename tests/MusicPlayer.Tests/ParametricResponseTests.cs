using System;
using System.Linq;
using System.Numerics;
using Xunit.Abstractions;
using static MusicPlayer.ParametricFilterType;

namespace MusicPlayer.Tests;

public class ParametricResponseTests(ITestOutputHelper log)
{
    [Theory]
    [InlineData(8000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    [InlineData(384000)]
    public void PoleRadiiAndResponseMatchPrewarpedAnalogPrototype(int rate)
    {
        double worstDb = 0, maxRadius = 0;
        foreach (var type in Enum.GetValues<ParametricFilterType>())
        foreach (double f in new double[] { 5, rate * 0.13, rate * 0.495 })
        foreach (double gain in new double[] { -24, 0, 24 })
        foreach (double q in new[] { 0.25, type == Peaking ? 16 : Math.Sqrt(0.5) })
        {
            var band = new ParametricBand(type, f, gain, q);
            var section = BiquadSection.Design(band, rate);
            Complex root = Complex.Sqrt(section.A1 * section.A1 - 4 * section.A2);
            foreach (Complex pole in new[] { (-section.A1 + root) / 2, (-section.A1 - root) / 2 })
            {
                Assert.True(pole.Magnitude < 1);
                maxRadius = Math.Max(maxRadius, pole.Magnitude);
            }
            var response = new ParametricEqualizerSettings(band).CreateResponse(rate);
            double[] frequencies = new double[33];
            response.FillLogFrequencies(frequencies, 0.01, rate * 0.5);
            foreach (double hz in frequencies.Append(0).Append(f))
            {
                double reference = PrototypeDb(band, hz, rate);
                double db = response.GetBandDecibels(0, hz);
                worstDb = Math.Max(worstDb, Math.Abs(db - reference));
            }
            Assert.Equal(type == Peaking ? gain : gain / 2, response.GetDecibels(f), 4);
        }
        log.WriteLine($"{rate} Hz: maximum pole radius {maxRadius:G12}, maximum prototype error {worstDb:G6} dB");
        Assert.InRange(worstDb, 0, 0.0001);
    }

    // Evaluate the analog transfer function at the bilinear-prewarped test
    // frequency. Independent of the digital coefficient and response code.
    private static double PrototypeDb(ParametricBand band, double hz, int rate)
    {
        if (hz == rate * 0.5)
            return band.Type == HighShelf ? band.GainDb : 0;
        double a = Math.Pow(10, band.GainDb / 40);
        Complex s = Complex.ImaginaryOne * (Math.Tan(Math.PI * hz / rate) / Math.Tan(Math.PI * band.FrequencyHz / rate));
        Complex damping = Math.Sqrt(a) * s / band.Q;
        Complex h = band.Type switch
        {
            Peaking => (s * s + a * s / band.Q + 1) / (s * s + s / (a * band.Q) + 1),
            LowShelf => a * (s * s + damping + a) / (a * s * s + damping + 1),
            _ => a * (a * s * s + damping + 1) / (s * s + damping + a)
        };
        return 20 * Math.Log10(h.Magnitude);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void RenderedMixedImpulseMatchesSnapshotAcrossLogGrid(int rate)
    {
        ParametricBand[] bands = [new(LowShelf, 67, 6), new(Peaking, 1940, -11, 4),
            new(Peaking, 2180, 7, 8), new(HighShelf, 10010, -9), new(Peaking, 1000, 24, Enabled: false)];
        float[] impulse = new float[rate / 2];
        impulse[0] = 1;
        var eq = new EqualizerSampleProvider(new ArraySampleProvider(impulse, rate, 1))
        {
            Mode = EqualizerMode.Parametric, ParametricSettings = new(bands)
        };
        eq.SetOutputLevel(0.5f);
        var response = eq.GetResponse();
        var rendered = new float[impulse.Length];
        Assert.Equal(rendered.Length, eq.Read(rendered));
        double[] grid = new double[65];
        response.FillLogFrequencies(grid, 1, rate * 0.5);
        Assert.Equal(1, grid[0]);
        Assert.Equal(rate * 0.5, grid[^1]);
        Assert.Equal(grid[1] / grid[0], grid[^1] / grid[^2], 10);
        double worst = 0;
        foreach (double hz in grid.Prepend(0))
        {
            Complex z = Complex.FromPolarCoordinates(1, -2 * Math.PI * hz / rate);
            Complex measured = Complex.Zero;
            for (int n = rendered.Length - 1; n >= 0; n--)
                measured = rendered[n] + z * measured;
            double expected = response.GetDecibels(hz, EqualizerResponseStage.Output);
            worst = Math.Max(worst, Math.Abs(20 * Math.Log10(measured.Magnitude) - expected));
            Assert.Equal(bands.Where(b => b.Enabled).Sum(b => PrototypeDb(b, hz, rate)), response.GetDecibels(hz), 5);
            Assert.Equal(0, response.GetBandDecibels(4, hz));
        }
        log.WriteLine($"{rate} Hz: maximum impulse-response error {worst:G6} dB");
        Assert.InRange(worst, 0, 0.00002);
    }

    [Fact]
    public void HeadroomIncludesCloselySpacedNarrowBellsAndShelves()
    {
        var settings = new ParametricEqualizerSettings(new(LowShelf, 1800, 6), new(Peaking, 1123, 18, 16),
            new(Peaking, 1131, 18, 16), new(HighShelf, 1000, 4));
        var response = settings.CreateResponse(48000);
        Assert.True(response.HeadroomDb < -39);
        double nearPeak = double.NegativeInfinity;
        for (int hz = 0; hz <= 24000; hz++)
            nearPeak = Math.Max(nearPeak, response.GetDecibels(hz, EqualizerResponseStage.WithHeadroom));
        Assert.InRange(nearPeak, -1.0, 1e-8);

        foreach (var type in Enum.GetValues<ParametricFilterType>())
        {
            Assert.Equal(0, new ParametricEqualizerSettings(new ParametricBand(type, 1234, -12)).CreateResponse(48000).HeadroomDb);
            var flat = new ParametricEqualizerSettings(new ParametricBand(type, 1234, 0)).CreateResponse(48000);
            Assert.Equal(0, flat.GetDecibels(1234, EqualizerResponseStage.WithHeadroom));
        }
    }

    [Fact]
    public void InvalidSettingsOrGraphQueriesFailBeforePublication()
    {
        var peak = new ParametricBand(Peaking, 1000, 6);
        foreach (double invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
        {
            Reject(peak with { FrequencyHz = invalid });
            Reject(peak with { Q = invalid });
            Reject(peak with { GainDb = invalid });
        }
        foreach (double invalid in new double[] { -1, 0, 4.99 }) Reject(peak with { FrequencyHz = invalid });
        foreach (double invalid in new double[] { -1, 0, 0.249, 16.01 }) Reject(peak with { Q = invalid });
        Reject(peak with { Type = (ParametricFilterType)123 });
        Reject(peak with { Type = LowShelf, Q = 0.71 });
        Reject(peak with { GainDb = -24.001 });
        Reject(peak with { GainDb = 24.001 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParametricEqualizerSettings(Enumerable.Repeat(peak, 17).ToArray()));

        ParametricBand[] caller = [peak];
        var saved = new ParametricEqualizerSettings(caller);
        caller[0] = peak with { GainDb = 0 };
        var eq = new EqualizerSampleProvider(new ArraySampleProvider([], 8000, 1)) { ParametricSettings = saved, Mode = EqualizerMode.Parametric };
        foreach (double hz in new[] { 3961.0, 4000, 8000 })
            Assert.Throws<ArgumentOutOfRangeException>(() => eq.ParametricSettings = new(peak with { FrequencyHz = hz }));
        Assert.Throws<ArgumentNullException>(() => eq.ParametricSettings = null!);
        Assert.Throws<ArgumentOutOfRangeException>(() => eq.Mode = (EqualizerMode)9);
        Assert.Same(saved, eq.ParametricSettings);
        var snapshot = eq.GetResponse();
        eq.SetEnabled(false);
        Assert.Equal(6, snapshot.GetDecibels(1000), 8);
        Assert.Equal(0, eq.GetResponse().GetBandDecibels(0, 1000));
        Assert.Equal(0, eq.GetResponse().GetDecibels(1000, EqualizerResponseStage.WithHeadroom));
        Assert.Throws<ArgumentOutOfRangeException>(() => saved.CreateResponse(0));
        foreach (double hz in new[] { -1.0, double.NaN, double.PositiveInfinity, 4000.1 })
            Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetDecibels(hz));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetBandDecibels(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetDecibels(0, (EqualizerResponseStage)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.FillLogFrequencies(new double[1], 1, 2));
        foreach (var range in new[] { (0, 1), (2, 1), (1, 4001) })
            Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.FillLogFrequencies(new double[2], range.Item1, range.Item2));
    }

    private static void Reject(ParametricBand band) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParametricEqualizerSettings(band));
}
