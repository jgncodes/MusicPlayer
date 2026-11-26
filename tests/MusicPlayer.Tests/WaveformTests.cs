using System;
using System.Linq;
using System.Threading;
using MusicPlayer;
using Xunit;

namespace MusicPlayer.Tests;

/// <summary>
/// Tests waveform generation using deterministic audio signals.
///
/// These tests do not require audio hardware.
/// </summary>
public class WaveformTests
{
    private const int SampleRate = 44100;
    private const int ExpectedBins = 400;

    // -----------------------------------------------
    // TEST 1: WAVEFORM LENGTH
    // -----------------------------------------------

    [Fact]
    public void GenerateWaveform_Returns400Bins()
    {
        float[] samples = TestAudioFactory.GenerateSineWave(
            frequencyHz: 440,
            sampleRate: SampleRate);

        using var file = new TemporaryWavFile(
            samples, SampleRate);

        using var cache = new TestDirectory();
        var service = new AudioAnalysisService(cache.Path);

        float[] waveform = service.GenerateWaveform(
            file.FilePath,
            CancellationToken.None);

        Assert.Equal(ExpectedBins, waveform.Length);
    }

    // -----------------------------------------------
    // TEST 2: SILENCE
    // -----------------------------------------------

    [Fact]
    public void GenerateWaveform_SilenceProducesZeroPeaks()
    {
        float[] samples = TestAudioFactory.GenerateSilence(
            sampleRate: SampleRate);

        using var file = new TemporaryWavFile(
            samples, SampleRate);

        using var cache = new TestDirectory();
        var service = new AudioAnalysisService(cache.Path);

        float[] waveform = service.GenerateWaveform(
            file.FilePath,
            CancellationToken.None);

        Assert.Equal(ExpectedBins, waveform.Length);

        Assert.All(waveform, peak =>
            Assert.Equal(0f, peak));
    }

    // -----------------------------------------------
    // TEST 3: SINE WAVE AMPLITUDE
    // -----------------------------------------------

    [Fact]
    public void GenerateWaveform_PreservesSineWaveAmplitude()
    {
        // Generate a sine wave with peak amplitude 0.5.
        float[] samples = TestAudioFactory.GenerateSineWave(
            frequencyHz: 440,
            amplitude: 0.5,
            sampleRate: SampleRate);

        using var file = new TemporaryWavFile(
            samples, SampleRate);

        using var cache = new TestDirectory();
        var service = new AudioAnalysisService(cache.Path);

        float[] waveform = service.GenerateWaveform(
            file.FilePath,
            CancellationToken.None);

        Assert.Equal(ExpectedBins, waveform.Length);

        // The strongest waveform bin should preserve
        // the original peak amplitude.
        float maximumPeak = waveform.Max();

        Assert.InRange(maximumPeak, 0.49f, 0.51f);
    }

    // -----------------------------------------------
    // TEST 4: SINGLE IMPULSE
    // -----------------------------------------------

    [Fact]
    public void GenerateWaveform_ImpulseAppearsNearMiddle()
    {
        // One second of silence.
        float[] samples = new float[SampleRate];

        // Add one transient at the halfway point.
        samples[SampleRate / 2] = 1.0f;

        using var file = new TemporaryWavFile(
            samples, SampleRate);

        using var cache = new TestDirectory();
        var service = new AudioAnalysisService(cache.Path);

        float[] waveform = service.GenerateWaveform(
            file.FilePath,
            CancellationToken.None);

        Assert.Equal(ExpectedBins, waveform.Length);

        // Find the bin containing the largest peak.
        int peakIndex = Array.IndexOf(
            waveform, waveform.Max());

        // The peak should be near the middle.
        Assert.InRange(peakIndex, 198, 202);

        // It should preserve the impulse amplitude.
        Assert.InRange(waveform[peakIndex], 0.99f, 1.0f);
    }

    // -----------------------------------------------
    // TEST 5: OUTPUT BOUNDS
    // -----------------------------------------------

    [Fact]
    public void GenerateWaveform_PeaksRemainWithinValidRange()
    {
        float[] samples = TestAudioFactory.GenerateNoise(
            seed: 42,
            amplitude: 0.8,
            sampleRate: SampleRate);

        using var file = new TemporaryWavFile(
            samples, SampleRate);

        using var cache = new TestDirectory();
        var service = new AudioAnalysisService(cache.Path);

        float[] waveform = service.GenerateWaveform(
            file.FilePath,
            CancellationToken.None);

        Assert.Equal(ExpectedBins, waveform.Length);

        Assert.All(waveform, peak =>
        {
            Assert.True(float.IsFinite(peak));
            Assert.InRange(peak, 0f, 1f);
        });
    }

    // -----------------------------------------------
    // TEST 6: CANCELLATION
    // -----------------------------------------------

    [Fact]
    public void GenerateWaveform_CanceledTokenThrows()
    {
        float[] samples = TestAudioFactory.GenerateSilence(
            sampleRate: SampleRate);

        using var file = new TemporaryWavFile(
            samples, SampleRate);

        using var cache = new TestDirectory();
        var service = new AudioAnalysisService(cache.Path);

        using var cancellation =
            new CancellationTokenSource();

        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            service.GenerateWaveform(
                file.FilePath,
                cancellation.Token));
    }

    // -----------------------------------------------
    // TEST 7: REPEATED ANALYSIS
    // -----------------------------------------------

    [Fact]
    public void GenerateWaveform_RepeatedCallsAreConsistent()
    {
        float[] samples = TestAudioFactory.GenerateSineWave(
            frequencyHz: 1000,
            amplitude: 0.7,
            sampleRate: SampleRate);

        using var file = new TemporaryWavFile(
            samples, SampleRate);

        using var cache = new TestDirectory();
        var service = new AudioAnalysisService(cache.Path);

        float[] first = service.GenerateWaveform(
            file.FilePath,
            CancellationToken.None);

        float[] second = service.GenerateWaveform(
            file.FilePath,
            CancellationToken.None);

        Assert.Equal(first, second);
    }
}
