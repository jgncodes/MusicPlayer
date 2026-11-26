using System.Numerics;

namespace MusicPlayer;

public enum EqualizerResponseStage { Filters, WithHeadroom, Output }

/// <summary>Immutable steady-state target. Output includes headroom, trim, volume and mute.</summary>
public sealed class EqualizerResponse
{
    private readonly EqualizerCurve _curve;
    private readonly double _outputDb;
    public int SampleRate { get; }
    public int BandCount { get; }
    public double HeadroomDb => 20 * Math.Log10(_curve.Gain);

    internal EqualizerResponse(EqualizerCurve curve, int sampleRate, int bandCount, float outputLevel)
    {
        _curve = curve;
        SampleRate = sampleRate;
        BandCount = bandCount;
        _outputDb = 20 * Math.Log10(outputLevel);
    }

    public double GetBandDecibels(int bandIndex, double frequencyHz)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)bandIndex, (uint)BandCount);
        Complex z = UnitDelay(frequencyHz);
        return !_curve.Bands.IsEmpty && _curve.Bands[bandIndex] is { } section
            ? 20 * Math.Log10(section.Response(z).Magnitude) : 0;
    }

    public double GetDecibels(double frequencyHz, EqualizerResponseStage stage = EqualizerResponseStage.Filters)
    {
        Complex z = UnitDelay(frequencyHz), response = Complex.One;
        foreach (var section in _curve.Sections)
            response *= section.Response(z);
        double filterDb = 20 * Math.Log10(response.Magnitude);
        return stage switch
        {
            EqualizerResponseStage.Filters => filterDb,
            EqualizerResponseStage.WithHeadroom => filterDb + HeadroomDb,
            EqualizerResponseStage.Output => filterDb + HeadroomDb + _outputDb,
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
    }

    public void FillLogFrequencies(Span<double> frequencies, double minimumHz, double maximumHz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frequencies.Length, 2);
        if (!(minimumHz > 0 && minimumHz < maximumHz && maximumHz <= SampleRate * 0.5))
            throw new ArgumentOutOfRangeException(nameof(minimumHz), "Require 0 < minimum < maximum ≤ Nyquist.");
        double start = Math.Log(minimumHz);
        double step = (Math.Log(maximumHz) - start) / (frequencies.Length - 1);
        frequencies[0] = minimumHz;
        for (int i = 1; i < frequencies.Length - 1; i++)
            frequencies[i] = Math.Exp(start + i * step);
        frequencies[^1] = maximumHz;
    }

    private Complex UnitDelay(double frequencyHz)
    {
        if (!(frequencyHz >= 0 && frequencyHz <= SampleRate * 0.5))
            throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        return Complex.FromPolarCoordinates(1, -2 * Math.PI * frequencyHz / SampleRate);
    }
}
