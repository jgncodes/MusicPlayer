using System;
using MusicPlayer;
using NAudio.Wave;
using Xunit;

namespace MusicPlayer.Tests;

public class EqualizerTests
{
    private const int SampleRate = 44100;
    private const double DurationSeconds = 1.0;

    // --------------------------------------------------
    // TEST 1: FLAT EQUALIZER
    // --------------------------------------------------

    [Fact]
    public void FlatEqualizer_PreservesOriginalAudio()
    {
        float[] input = TestAudioFactory.GenerateSineWave(
            frequencyHz: 1000,
            amplitude: 0.5,
            sampleRate: SampleRate);

        float[] output = ProcessAudio(input);

        Assert.Equal(input.Length, output.Length);

        // Digital filters use floating-point calculations.
        // Allow a small numerical tolerance.
        for (int i = 0; i < input.Length; i++)
        {
            Assert.InRange(
                Math.Abs(output[i] - input[i]),
                0f,
                0.0001f);
        }
    }

    // --------------------------------------------------
    // TEST 2: DISABLED EQUALIZER
    // --------------------------------------------------

    [Fact]
    public void DisabledEqualizer_BypassesAllProcessing()
    {
        float[] input = TestAudioFactory.GenerateSineWave(
            frequencyHz: 1000,
            amplitude: 0.5);

        var source = new ArraySampleProvider(
            input, SampleRate, 1);

        var equalizer = new EqualizerSampleProvider(source);

        // Apply a significant boost.
        equalizer.SetBandGain(5, 12f);

        // Bypass the equalizer.
        equalizer.SetEnabled(false);

        float[] output = ReadAll(equalizer, input.Length);

        // Disabled processing must return exactly the
        // samples supplied by the source.
        Assert.Equal(input, output);
    }

    // --------------------------------------------------
    // TEST 3: FREQUENCY BOOST
    // --------------------------------------------------

    [Fact]
    public void Boosting1kHz_EmphasizesTargetFrequency()
    {
        // Test both a target and a distant frequency.
        double targetBefore = MeasureOutputRms(1000, 0f);
        double targetAfter = MeasureOutputRms(1000, 6f);

        double bassBefore = MeasureOutputRms(100, 0f);
        double bassAfter = MeasureOutputRms(100, 6f);

        // The +6 dB boost also introduces headroom
        // attenuation. Compare relative responses,
        // not absolute volume.
        double targetRatio = targetAfter / targetBefore;
        double bassRatio = bassAfter / bassBefore;

        Assert.True(
            targetRatio > bassRatio * 1.2,
            $"Expected 1 kHz to be emphasized. " +
            $"Target ratio: {targetRatio:F3}, " +
            $"bass ratio: {bassRatio:F3}");
    }

    // --------------------------------------------------
    // TEST 4: FREQUENCY CUT
    // --------------------------------------------------

    [Fact]
    public void Cutting1kHz_ReducesTargetFrequency()
    {
        double before = MeasureOutputRms(1000, 0f);
        double after = MeasureOutputRms(1000, -6f);

        Assert.True(
            after < before * 0.8,
            $"Expected attenuation at 1 kHz. " +
            $"Before: {before:F4}, After: {after:F4}");
    }

    // --------------------------------------------------
    // TEST 5: STEREO CHANNEL INDEPENDENCE
    // --------------------------------------------------

    [Fact]
    public void StereoEqualizer_DoesNotMixChannels()
    {
        float[] left = TestAudioFactory.GenerateSineWave(
            frequencyHz: 1000,
            amplitude: 0.5);

        // Interleave stereo samples: L, R, L, R...
        float[] stereo = new float[left.Length * 2];

        for (int i = 0; i < left.Length; i++)
        {
            stereo[2 * i] = left[i];
            stereo[2 * i + 1] = 0f;
        }

        float[] output = ProcessAudio(
            stereo,
            channels: 2,
            bandIndex: 5,
            gainDb: 6f);

        // Right channel should stay silent.
        for (int i = 1; i < output.Length; i += 2)
        {
            Assert.InRange(
                Math.Abs(output[i]), 0f, 0.00001f);
        }

        // Left channel must still contain audio.
        Assert.True(Math.Abs(output[1000]) > 0.01f);
    }

    // --------------------------------------------------
    // TEST 6: GAIN LIMITS
    // --------------------------------------------------

    [Theory]
    [InlineData(100f, 12f)]
    [InlineData(-100f, -12f)]
    public void SetBandGain_ClampsValuesToAllowedRange(
        float requestedGain,
        float expectedGain)
    {
        float[] input = TestAudioFactory.GenerateSineWave(
            frequencyHz: 1000,
            amplitude: 0.5);

        float[] clampedOutput = ProcessAudio(
            input,
            bandIndex: 5,
            gainDb: requestedGain);

        float[] expectedOutput = ProcessAudio(
            input,
            bandIndex: 5,
            gainDb: expectedGain);

        // An out-of-range gain should produce the same
        // output as its corresponding limit.
        Assert.Equal(expectedOutput.Length, clampedOutput.Length);

        for (int i = 0; i < expectedOutput.Length; i++)
        {
            Assert.Equal(expectedOutput[i], clampedOutput[i]);
        }
    }

    // --------------------------------------------------
    // TEST 7: INVALID BAND INDEX
    // --------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    [InlineData(100)]
    public void SetBandGain_InvalidBandThrows(int band)
    {
        float[] input = new float[100];

        var source = new ArraySampleProvider(
            input, SampleRate, 1);

        var equalizer = new EqualizerSampleProvider(source);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            equalizer.SetBandGain(band, 6f));
    }

    // --------------------------------------------------
    // TEST 8: OUTPUT HEADROOM
    // --------------------------------------------------

    [Fact]
    public void PositiveBoost_ReducesUnaffectedFrequency()
    {
        // Boost the 1 kHz band while measuring 100 Hz.
        double flatRms = MeasureOutputRms(100, 0f);
        double boostedRms = MeasureOutputRms(100, 6f);

        // The 100 Hz signal is far from the boosted band,
        // so output attenuation should dominate.
        Assert.True(
            boostedRms < flatRms * 0.8,
            $"Expected headroom attenuation. " +
            $"Flat: {flatRms:F4}, Boosted: {boostedRms:F4}");
    }

    // --------------------------------------------------
    // TEST 9: WAVE FORMAT
    // --------------------------------------------------

    [Fact]
    public void Equalizer_PreservesSourceWaveFormat()
    {
        float[] input = new float[1000];

        var source = new ArraySampleProvider(
            input,
            sampleRate: 44100,
            channels: 2);

        var equalizer = new EqualizerSampleProvider(source);

        Assert.Equal(44100, equalizer.WaveFormat.SampleRate);
        Assert.Equal(2, equalizer.WaveFormat.Channels);
    }

    // ==================================================
    // SHARED TEST HELPERS
    // ==================================================

    /// <summary>
    /// Pass samples through the real equalizer.
    /// </summary>
    private static float[] ProcessAudio(
        float[] input,
        int channels = 1,
        int bandIndex = -1,
        float gainDb = 0f)
    {
        var source = new ArraySampleProvider(
            input, SampleRate, channels);

        var equalizer = new EqualizerSampleProvider(source);

        if (bandIndex >= 0)
            equalizer.SetBandGain(bandIndex, gainDb);

        return ReadAll(equalizer, input.Length);
    }

    /// <summary>
    /// Read every sample from a sample provider.
    /// </summary>
    private static float[] ReadAll(
        ISampleProvider provider,
        int sampleCount)
    {
        float[] output = new float[sampleCount];

        int totalRead = 0;

        while (totalRead < sampleCount)
        {
            int read = provider.Read(
                output.AsSpan(totalRead));

            if (read == 0)
                break;

            totalRead += read;
        }

        Assert.Equal(sampleCount, totalRead);

        return output;
    }

    /// <summary>
    /// Calculate the RMS of a processed sine wave,
    /// ignoring the first 100 ms of filter startup.
    ///
    /// bandIndex 5 corresponds to the 1000 Hz band.
    /// </summary>
    private static double MeasureOutputRms(
        double frequencyHz,
        float gainDb)
    {
        float[] input = TestAudioFactory.GenerateSineWave(
            frequencyHz: frequencyHz,
            amplitude: 0.5,
            sampleRate: SampleRate,
            durationSeconds: DurationSeconds);

        float[] output = ProcessAudio(
            input,
            bandIndex: 5,
            gainDb: gainDb);

        int start = SampleRate / 10;
        double sumSquares = 0.0;

        for (int i = start; i < output.Length; i++)
            sumSquares += (double)output[i] * output[i];

        return Math.Sqrt(
            sumSquares / (output.Length - start));
    }

    // ==================================================
    // IN-MEMORY AUDIO SOURCE
    // ==================================================

    /// <summary>
    /// Supplies existing float samples to NAudio.
    ///
    /// No audio device or WAV file is required.
    /// </summary>
    private sealed class ArraySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public WaveFormat WaveFormat { get; }

        public ArraySampleProvider(
            float[] samples,
            int sampleRate,
            int channels)
        {
            _samples = samples;

            WaveFormat =
                WaveFormat.CreateIeeeFloatWaveFormat(
                    sampleRate, channels);
        }

        public int Read(Span<float> buffer)
        {
            int available = _samples.Length - _position;
            int count = Math.Min(buffer.Length, available);

            _samples.AsSpan(_position, count).CopyTo(buffer);

            _position += count;

            return count;
        }
    }
}
