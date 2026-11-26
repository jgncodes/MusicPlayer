using System;
using System.Linq;
using System.Numerics;
using NAudio.Wave;
using Xunit.Abstractions;

namespace MusicPlayer.Tests;

public class FilterResponseTests(ITestOutputHelper log)
{
    [Fact]
    public void QuietMultitoneScalesLinearlyAndImpulseTailDecays()
    {
        const int rate = 48000;
        const float scale = 1f / 1048576;
        float[] signal = TestAudioFactory.GenerateSineWave(31.25, 0.4, rate, 2);
        float[] treble = TestAudioFactory.GenerateSineWave(16000, 0.4, rate, 2);
        for (int i = 0; i < rate; i++)
            signal[i] += treble[i];
        Array.Clear(signal, rate, rate);
        signal[rate] = 1;
        float[] quiet = signal.Select(x => x * scale).ToArray();
        var loudEq = new EqualizerSampleProvider(new ArraySampleProvider(signal, rate, 1));
        var quietEq = new EqualizerSampleProvider(new ArraySampleProvider(quiet, rate, 1));
        float[] curve = [12, -12, 12, -12, 12, -12, 12, -12, 12, -12];
        for (int band = 0; band < curve.Length; band++)
        {
            loudEq.SetBandGain(band, curve[band]);
            quietEq.SetBandGain(band, curve[band]);
        }
        loudEq.Read(signal);
        quietEq.Read(quiet);
        for (int i = 0; i < rate; i++)
            Assert.Equal(signal[i] * scale, quiet[i]);
        Assert.Contains(signal.AsSpan(rate + 1, 100).ToArray(), x => Math.Abs(x) > 1e-6);
        Assert.All(signal[^100..], x => Assert.InRange(Math.Abs(x), 0, 1e-12f));
    }

    [Theory]
    [InlineData(16000)]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    [InlineData(192000)]
    public void ImpulseSpectrumMatchesCookbookCascade(int rate)
    {
        float[] gains = [-8, 4, -5, 3, 6, -4, 8, -6, 5, 9];
        float[] impulse = new float[rate];
        impulse[0] = 1;
        var eq = new EqualizerSampleProvider(new ArraySampleProvider(impulse, rate, 1));
        for (int i = 0; i < gains.Length; i++)
            eq.SetBandGain(i, gains[i]);
        float[] response = new float[rate];
        Assert.Equal(rate, eq.Read(response));
        double dc = response.Sum(x => (double)x); // Every peak section is unity at DC.
        double worst = 0;
        foreach (double hz in EqualizerSampleProvider.Frequencies.Select(f => (double)f)
            .Concat(new[] { 12.0, 88.0, 740.0, 11300.0, rate * 0.49 })
            .Where(f => f < rate / 2.0))
        {
            Complex z = Complex.FromPolarCoordinates(1, -2 * Math.PI * hz / rate);
            Complex expected = Complex.One;
            for (int i = 0; i < gains.Length; i++)
            {
                double center = EqualizerSampleProvider.Frequencies[i];
                if (center >= rate * 0.45)
                    continue;
                double w = 2 * Math.PI * center / rate;
                double a = Math.Pow(10, gains[i] / 40.0), alpha = Math.Sin(w) / 2.8;
                Complex common = 1 - 2 * Math.Cos(w) * z + z * z;
                Complex delta = alpha * (1 - z * z);
                expected *= (common + a * delta) / (common + delta / a);
            }
            Complex h = Complex.Zero;
            for (int i = response.Length - 1; i >= 0; i--)
                h = response[i] + z * h;
            double error = 20 * Math.Log10(h.Magnitude / (dc * expected.Magnitude));
            worst = Math.Max(worst, Math.Abs(error));
        }
        log.WriteLine($"{rate} Hz: max response error {worst:G8} dB, DC trim {20 * Math.Log10(dc):F5} dB");
        Assert.InRange(worst, 0, 0.0001);
    }

    [Fact]
    public void CoherentSettledSineHasLowResidualAndDc()
    {
        const int rate = 48000;
        float[] tone = TestAudioFactory.GenerateSineWave(1000, 0.5, rate);
        var eq = new EqualizerSampleProvider(new ArraySampleProvider(tone, rate, 1));
        eq.SetBandGain(5, -6);
        float[] output = new float[rate];
        Assert.Equal(rate, eq.Read(output));
        // 500 complete cycles after 500 cycles of settling. Fit both quadratures.
        int n = rate / 2;
        double s = 0, c = 0, mean = 0;
        for (int i = n; i < rate; i++)
        {
            double w = 2 * Math.PI * 1000 * i / rate;
            s += output[i] * Math.Sin(w);
            c += output[i] * Math.Cos(w);
            mean += output[i];
        }
        s *= 2.0 / n;
        c *= 2.0 / n;
        mean /= n;
        double residual = 0;
        for (int i = n; i < rate; i++)
        {
            double w = 2 * Math.PI * 1000 * i / rate;
            double err = output[i] - mean - s * Math.Sin(w) - c * Math.Cos(w);
            residual += err * err;
        }
        double db = 10 * Math.Log10((residual / n) / ((s * s + c * c) / 2));
        log.WriteLine($"1 kHz -6 dB: RMS {Math.Sqrt((s*s+c*c)/2):F8}, residual {db:F2} dBc, DC {mean:G8}");
        Assert.InRange(db, double.NegativeInfinity, -100);
        Assert.InRange(Math.Abs(mean), 0, 1e-7);
    }
}
