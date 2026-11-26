namespace MusicPlayer;

// Sample-rate-specific geometry. Only the settings writer designs curves.
internal sealed class GraphicEqualizerDesign
{
    private const double Q = 1.4;
    private readonly Band[] _bands;
    internal int ActiveBandCount => _bands.Length;
    private readonly record struct Band(double Cosine, double Alpha, double[] Detuning);

    internal GraphicEqualizerDesign(int rate)
    {
        var centers = EqualizerSampleProvider.Frequencies.TakeWhile(f => f < 0.45 * rate).ToArray();
        // Include DC and Nyquist. Subdivide each octave to tighten the upper bound.
        List<double> edges = [0];
        for (int i = 0; i < centers.Length; i++)
        {
            edges.Add(centers[i]);
            if (i + 1 < centers.Length)
                for (int step = 1; step < 12; step++)
                    edges.Add(centers[i] * Math.Pow(2, step / 12.0));
        }
        edges.Add(rate / 2.0);

        _bands = new Band[centers.Length];
        for (int i = 0; i < centers.Length; i++)
        {
            double w = 2 * Math.PI * centers[i] / rate;
            double cosine = Math.Cos(w), alpha = Math.Sin(w) / (2 * Q);
            double[] detuning = new double[edges.Count - 1];
            for (int j = 0; j < detuning.Length; j++)
            {
                // The magnitude of a boosted RBJ peak rises to its center, then falls.
                // This point maximizes THIS section on the entire closed interval.
                double nearest = Math.Clamp((double)centers[i], edges[j], edges[j + 1]);
                double angle = 2 * Math.PI * nearest / rate;
                double ratio = (Math.Cos(angle) - cosine) / (alpha * Math.Sin(angle));
                detuning[j] = ratio * ratio;
            }
            _bands[i] = new Band(cosine, alpha, detuning);
        }
    }

    internal EqualizerCurve Create(ReadOnlySpan<float> gains)
    {
        List<PeakSection> sections = new(_bands.Length);
        if (_bands.Length == 0)
            return EqualizerCurve.Flat;
        Span<double> upperPower = stackalloc double[_bands[0].Detuning.Length];
        upperPower.Fill(1);
        for (int i = 0; i < _bands.Length; i++)
        {
            if (gains[i] == 0)
                continue;
            Band band = _bands[i];
            double a = Math.Pow(10, gains[i] / 40.0);
            double a0 = 1 + band.Alpha / a;
            sections.Add(new PeakSection((1 + band.Alpha * a) / a0, -2 * band.Cosine / a0,
                (1 - band.Alpha * a) / a0, (1 - band.Alpha / a) / a0));
            if (gains[i] > 0)
            {
                // Cuts have magnitude <= 1. Products of per-section maxima bound
                // the cascade even when overlapping peaks fall between grid points.
                double a2 = a * a;
                for (int j = 0; j < upperPower.Length; j++)
                {
                    double d = band.Detuning[j];
                    upperPower[j] *= (d + a2) / (d + 1 / a2);
                }
            }
        }
        if (sections.Count == 0)
            return EqualizerCurve.Flat;
        double peakPower = 1;
        foreach (double power in upperPower)
            peakPower = Math.Max(peakPower, power);
        return new EqualizerCurve(sections.ToArray(), 1 / Math.Sqrt(peakPower));
    }
}

// For a normalized cookbook peak, a1 == b1. Coefficients and delay lines are double.
internal readonly record struct PeakSection(double B0, double B1, double B2, double A2);
internal sealed record EqualizerCurve(PeakSection[] Sections, double Gain)
{
    internal static readonly EqualizerCurve Flat = new([], 1);
}

internal sealed class FilterBank(int channels)
{
    private struct Delay { internal double Z1, Z2; }
    private readonly Delay[] _states = new Delay[channels * EqualizerSampleProvider.BandCount];
    internal EqualizerCurve Curve { get; private set; } = EqualizerCurve.Flat;

    internal void Start(EqualizerCurve curve)
    {
        Curve = curve;
        Array.Clear(_states);
    }

    internal void TransitionFrom(FilterBank previous, EqualizerCurve curve)
    {
        Start(curve);
        int shared = 0;
        while (shared < curve.Sections.Length && shared < previous.Curve.Sections.Length &&
               curve.Sections[shared] == previous.Curve.Sections[shared])
            shared++;
        // Identical upstream sections have identical inputs and histories. Keeping
        // them avoids truncating a long bass decay when only treble changes.
        if (shared > 0)
        {
            for (int offset = 0; offset < _states.Length; offset += EqualizerSampleProvider.BandCount)
                previous._states.AsSpan(offset, shared).CopyTo(_states.AsSpan(offset, shared));
        }
    }

    internal double Process(double sample, int channel)
    {
        int index = channel * EqualizerSampleProvider.BandCount;
        foreach (var section in Curve.Sections)
        {
            ref Delay state = ref _states[index++];
            double output = section.B0 * sample + state.Z1;
            state.Z1 = section.B1 * (sample - output) + state.Z2;
            state.Z2 = section.B2 * sample - section.A2 * output;
            sample = output;
        }
        return sample * Curve.Gain;
    }
}
