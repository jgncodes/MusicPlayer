using System.Numerics;

namespace MusicPlayer;

// Denominator 1 + A1 z^-1 + A2 z^-2. Both design and delay lines use doubles.
internal readonly record struct BiquadSection(double B0, double B1, double B2, double A1, double A2)
{
    internal static BiquadSection Peak(double cosine, double alpha, double a) =>
        Normalize(1 + alpha * a, -2 * cosine, 1 - alpha * a,
                  1 + alpha / a, -2 * cosine, 1 - alpha / a);

    internal static BiquadSection Design(ParametricBand band, int rate)
    {
        (double sine, double cosine) = Math.SinCos(2 * Math.PI * band.FrequencyHz / rate);
        double a = Math.Pow(10, band.GainDb / 40);
        double alpha = sine / (2 * band.Q);
        if (band.Type == ParametricFilterType.Peaking)
            return Peak(cosine, alpha, a);

        double p = a + 1, m = a - 1, beta = 2 * Math.Sqrt(a) * alpha;
        return band.Type == ParametricFilterType.LowShelf
            ? Normalize(a * (p - m * cosine + beta), 2 * a * (m - p * cosine), a * (p - m * cosine - beta),
                        p + m * cosine + beta, -2 * (m + p * cosine), p + m * cosine - beta)
            : Normalize(a * (p + m * cosine + beta), -2 * a * (m + p * cosine), a * (p + m * cosine - beta),
                        p - m * cosine + beta, 2 * (m - p * cosine), p - m * cosine - beta);
    }

    private static BiquadSection Normalize(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        var s = new BiquadSection(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
        // Strict second-order Jury inequalities for both poles inside the unit circle.
        if (!(Math.Abs(s.A2) < 1 && 1 + s.A1 + s.A2 > 0 && 1 - s.A1 + s.A2 > 0 &&
              double.IsFinite(s.B0) && double.IsFinite(s.B1) && double.IsFinite(s.B2)))
            throw new ArgumentException("The coefficients do not define a finite stable biquad.");
        return s;
    }

    internal Complex Response(Complex z) =>
        (B0 + z * (B1 + z * B2)) / (1 + z * (A1 + z * A2));
}
