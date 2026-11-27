namespace MusicPlayer;

internal enum ResponseOverlay { None, Headroom, Output, SelectedBand }

internal readonly record struct PlotScale
{
    internal double LowHz { get; }
    internal double HighHz { get; }
    internal double LowDb { get; }
    internal double HighDb { get; }
    internal double Width { get; }
    internal double Height { get; }
    private readonly double _logSpan;

    internal PlotScale(double lowHz, double highHz, double lowDb, double highDb, double width, double height)
    {
        if (!(lowHz > 0 && highHz > lowHz && highDb > lowDb && width > 0 && height > 0) ||
            !double.IsFinite(highHz) || !double.IsFinite(lowDb) || !double.IsFinite(highDb) ||
            !double.IsFinite(width) || !double.IsFinite(height))
            throw new ArgumentOutOfRangeException(nameof(width), "Require finite, ordered axes and positive plot dimensions.");
        (LowHz, HighHz, LowDb, HighDb, Width, Height) = (lowHz, highHz, lowDb, highDb, width, height);
        _logSpan = Math.Log(highHz / lowHz);
    }

    internal double X(double hz) => Width * Math.Log(Math.Clamp(hz, LowHz, HighHz) / LowHz) / _logSpan;
    internal double Hz(double x) => Math.Clamp(LowHz * Math.Exp(Math.Clamp(x / Width, 0, 1) * _logSpan), LowHz, HighHz);
    internal double Y(double db) => Height * (HighDb - Math.Clamp(db, LowDb, HighDb)) / (HighDb - LowDb);
    internal double Db(double y) => HighDb - Math.Clamp(y / Height, 0, 1) * (HighDb - LowDb);
}

// Samples the immutable DSP target. All gain stages after the filters are
// frequency-independent, so one API probe supplies their exact vertical offset.
internal sealed record ResponsePlot(double[] Frequencies, double[] Filters, double[]? Overlay,
    bool Silent, double LowDb, double HighDb)
{
    internal static ResponsePlot Sample(EqualizerResponse response, IReadOnlyList<ParametricBand> bands,
        int selected, ResponseOverlay overlay, int resolution)
    {
        int rate = response.SampleRate;
        double low = ParametricEqualizerSettings.MinimumHz(rate), high = ParametricEqualizerSettings.MaximumHz(rate);
        var grid = new double[Math.Clamp(resolution, 256, 512)];
        response.FillLogFrequencies(grid, low, high);
        var frequencies = new List<double>(grid.Length + 5 * bands.Count);
        frequencies.AddRange(grid);
        foreach (var band in bands)
        {
            if (!band.Enabled || !ParametricEqualizerSettings.IsAvailable(band, rate))
                continue;
            frequencies.Add(band.FrequencyHz);
            // Finer probes around narrow bells, including the warped bandwidth near Nyquist.
            if (band.Type != ParametricFilterType.Peaking || band.Q < 2)
                continue;
            double width = rate * Math.Sin(2 * Math.PI * band.FrequencyHz / rate) / (2 * Math.PI * band.Q);
            foreach (double fraction in (ReadOnlySpan<double>)[-1, -0.25, 0.25, 1])
                frequencies.Add(Math.Clamp(band.FrequencyHz + width * fraction, low, high));
        }
        frequencies.Sort();
        double[] hz = frequencies.ToArray();
        double[] filters = new double[hz.Length];
        bool individual = overlay == ResponseOverlay.SelectedBand && selected >= 0 && selected < response.BandCount;
        double offset = overlay switch
        {
            ResponseOverlay.Headroom => response.HeadroomDb,
            ResponseOverlay.Output => response.GetDecibels(low, EqualizerResponseStage.Output) - response.GetDecibels(low),
            _ => 0
        };
        bool silent = double.IsNegativeInfinity(offset);
        double[]? second = !silent && (individual || overlay is ResponseOverlay.Headroom or ResponseOverlay.Output)
            ? new double[hz.Length] : null;
        double min = -24, max = 24;
        for (int i = 0; i < hz.Length; i++)
        {
            double db = filters[i] = response.GetDecibels(hz[i]);
            double other = db;
            if (second != null)
                other = second[i] = individual ? response.GetBandDecibels(selected, hz[i]) : db + offset;
            min = Math.Min(min, Math.Min(db, other));
            max = Math.Max(max, Math.Max(db, other));
        }
        return new(hz, filters, second, silent, 12 * Math.Floor((min + 1e-8) / 12), 12 * Math.Ceiling((max - 1e-8) / 12));
    }
}
