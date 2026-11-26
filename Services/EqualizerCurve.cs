namespace MusicPlayer;

internal sealed class EqualizerCurve(BiquadSection?[] bands, double gain)
{
    internal static readonly EqualizerCurve Flat = new([], 1);
    internal double Gain { get; } = gain;
    internal ReadOnlySpan<BiquadSection?> Bands => bands;
    private readonly BiquadSection[] _sections = bands.Where(b => b.HasValue).Select(b => b.GetValueOrDefault()).ToArray();
    internal ReadOnlySpan<BiquadSection> Sections => _sections;
}

internal sealed class FilterBank(int channels)
{
    private struct Delay { internal double Z1, Z2; }
    private readonly Delay[] _states = new Delay[channels * ParametricEqualizerSettings.MaxBands];
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
            for (int offset = 0; offset < _states.Length; offset += ParametricEqualizerSettings.MaxBands)
                previous._states.AsSpan(offset, shared).CopyTo(_states.AsSpan(offset, shared));
        }
    }

    internal double Process(double sample, int channel)
    {
        int index = channel * ParametricEqualizerSettings.MaxBands;
        foreach (ref readonly var section in Curve.Sections)
        {
            ref Delay state = ref _states[index++];
            double output = section.B0 * sample + state.Z1;
            state.Z1 = section.B1 * sample - section.A1 * output + state.Z2;
            state.Z2 = section.B2 * sample - section.A2 * output;
            sample = output;
        }
        return sample * Curve.Gain;
    }
}
