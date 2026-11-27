using System.Numerics;

namespace MusicPlayer;

public enum EqualizerMode { Graphic, Parametric }
public enum ParametricFilterType { Peaking, LowShelf, HighShelf }

/// <summary>Q is the cookbook Q for bells and the damping Q for shelves. Shelf Q at 1/√2 gives slope S=1.</summary>
public readonly record struct ParametricBand(ParametricFilterType Type, double FrequencyHz,
    double GainDb, double Q = 0.7, bool Enabled = true);

public sealed class ParametricEqualizerSettings
{
    public const int MaxBands = 16;
    public static ParametricEqualizerSettings Flat { get; } = new();
    public IReadOnlyList<ParametricBand> Bands { get; }

    internal static double MinimumHz(int rate) => Math.Max(5, rate * 1e-5);
    internal static double MaximumHz(int rate) => rate * 0.495;
    internal static double MaximumQ(ParametricFilterType type) =>
        type == ParametricFilterType.Peaking ? 16 : Math.Sqrt(0.5);
    internal static bool IsAvailable(ParametricBand band, int rate) =>
        band.FrequencyHz >= MinimumHz(rate) && band.FrequencyHz <= MaximumHz(rate);

    public ParametricEqualizerSettings(params ReadOnlySpan<ParametricBand> bands)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bands.Length, MaxBands);
        foreach (var band in bands)
        {
            if (!Enum.IsDefined(band.Type))
                throw new ArgumentOutOfRangeException(nameof(bands), "Unknown filter type.");
            if (!double.IsFinite(band.FrequencyHz) || band.FrequencyHz < 5)
                throw new ArgumentOutOfRangeException(nameof(bands), "A filter frequency must be finite and at least 5 Hz.");
            if (!double.IsFinite(band.GainDb) || Math.Abs(band.GainDb) > 24)
                throw new ArgumentOutOfRangeException(nameof(bands), "Gain must be within ±24 dB.");
            double maxQ = MaximumQ(band.Type);
            if (!double.IsFinite(band.Q) || band.Q < 0.25 || band.Q > maxQ)
                throw new ArgumentOutOfRangeException(nameof(bands), $"Q must be in [0.25, {maxQ}].");
        }
        Bands = Array.AsReadOnly(bands.ToArray());
    }

    /// <summary>Design a standalone filter/headroom response at unity output level.</summary>
    public EqualizerResponse CreateResponse(int sampleRate) =>
        new(Design(sampleRate), sampleRate, Bands.Count, 1);

    // Retained track preferences can cross a format change. Only that path may omit
    // unavailable frequencies. New edits and offline designs use strict validation.
    internal EqualizerCurve Design(int sampleRate, bool omitUnavailable = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (Bands.Count == 0)
            return EqualizerCurve.Flat;
        var bands = new BiquadSection?[Bands.Count];
        List<double> centers = [];
        bool hasBoost = false;
        for (int i = 0; i < bands.Length; i++)
        {
            ParametricBand band = Bands[i];
            if (!IsAvailable(band, sampleRate))
            {
                if (omitUnavailable)
                    continue;
                throw new ArgumentOutOfRangeException(nameof(Bands), $"Band {i} is outside the usable range at {sampleRate} Hz.");
            }
            if (!band.Enabled || band.GainDb == 0)
                continue;
            bands[i] = BiquadSection.Design(band, sampleRate);
            centers.Add(band.FrequencyHz);
            hasBoost |= band.GainDb > 0;
        }
        if (centers.Count == 0)
            return EqualizerCurve.Flat;
        double gain = hasBoost ? CalculateHeadroom(bands, centers, sampleRate) : 1;
        return new EqualizerCurve(bands, gain);
    }

    private static double CalculateHeadroom(BiquadSection?[] bands, List<double> edges, int rate)
    {
        // Every bell turns at its center and every supported shelf is monotone.
        // On each interval, the larger endpoint is an upper bound for each section.
        edges.Add(0);
        edges.Add(rate * 0.5);
        double semitone = Math.Pow(2, 1.0 / 12);
        for (double f = Math.Max(5, rate * 1e-5); f < rate * 0.5; f *= semitone)
            edges.Add(f);
        edges.Sort();
        Span<double> previous = stackalloc double[bands.Length];
        double maxGain = 1;
        for (int j = 0; j < edges.Count; j++)
        {
            Complex z = Complex.FromPolarCoordinates(1, -2 * Math.PI * edges[j] / rate);
            double bound = 1;
            for (int i = 0; i < bands.Length; i++)
            {
                if (bands[i] is not { } section)
                    continue;
                double magnitude = section.Response(z).Magnitude;
                if (j > 0)
                    bound *= Math.Max(magnitude, previous[i]);
                previous[i] = magnitude;
            }
            maxGain = Math.Max(maxGain, bound);
        }
        return 1 / maxGain;
    }
}
