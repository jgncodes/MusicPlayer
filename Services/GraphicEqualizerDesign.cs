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
        var sections = new BiquadSection?[EqualizerSampleProvider.BandCount];
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
            sections[i] = BiquadSection.Peak(band.Cosine, band.Alpha, a);
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
        if (!sections.Any(s => s.HasValue))
            return EqualizerCurve.Flat;
        double peakPower = 1;
        foreach (double power in upperPower)
            peakPower = Math.Max(peakPower, power);
        return new EqualizerCurve(sections, 1 / Math.Sqrt(peakPower));
    }
}
