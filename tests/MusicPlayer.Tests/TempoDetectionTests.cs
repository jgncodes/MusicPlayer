
using System;
using System.Threading;
using MusicPlayer;
using Xunit;
using Xunit.Abstractions;

namespace MusicPlayer.Tests;

public class TempoDetectionTests
{
    private const int SampleRate = 44100;

    // Long enough to satisfy the estimator's 8-second
    // minimum and contain several repeating beats.
    private const double TestDurationSeconds = 12.0;

    private readonly ITestOutputHelper _output;

    public TempoDetectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // --------------------------------------------------
    // TEST 1: SILENCE
    // --------------------------------------------------

    [Fact]
    public void EstimateTempo_Silence_ReturnsNull()
    {
        // Arrange: twelve seconds with no audio energy.
        float[] samples = TestAudioFactory.GenerateSilence(
            sampleRate: SampleRate,
            durationSeconds: TestDurationSeconds);

        using var file = new TemporaryWavFile(
            samples, SampleRate);

        using var cache = new TestDirectory();
        var service = new AudioAnalysisService(cache.Path);

        // Act
        TempoEstimate? result = service.EstimateTempo(
            file.FilePath,
            CancellationToken.None);

        // Assert: no rhythmic information exists.
        Assert.Null(result);
    }

    // --------------------------------------------------
    // TEST 2: AUDIO TOO SHORT
    // --------------------------------------------------

    [Fact]
    public void EstimateTempo_ShortAudio_ReturnsNull()
    {
        // A clear beat is present, but the recording
        // is shorter than the supported analysis window.
        float[] samples = TestAudioFactory.GenerateBeatPulses(
            bpm: 120,
            durationSeconds: 4.0,
            sampleRate: SampleRate);

        using var file = new TemporaryWavFile(
            samples, SampleRate);

        using var cache = new TestDirectory();
        var service = new AudioAnalysisService(cache.Path);

        TempoEstimate? result = service.EstimateTempo(
            file.FilePath,
            CancellationToken.None);

        Assert.Null(result);
    }

    // --------------------------------------------------
    // TEST 3: KNOWN TEMPOS
    // --------------------------------------------------

    [Theory]
    [InlineData(80.0)]
    [InlineData(100.0)]
    [InlineData(120.0)]
    [InlineData(160.0)]
    public void EstimateTempo_BeatPulses_DetectsRhythm(
        double expectedBpm)
    {
        // Arrange: generate a predictable sequence
        // of short transients at the requested BPM.
        float[] samples = TestAudioFactory.GenerateBeatPulses(
            bpm: expectedBpm,
            durationSeconds: TestDurationSeconds,
            sampleRate: SampleRate);

        using var file = new TemporaryWavFile(
            samples, SampleRate);

        using var cache = new TestDirectory();
        var service = new AudioAnalysisService(cache.Path);

        // Act: use the real BPM estimator.
        TempoEstimate? result = service.EstimateTempo(
            file.FilePath,
            CancellationToken.None);

        // Record the estimate for debugging and evaluation.
        _output.WriteLine(
            $"Expected tempo: {expectedBpm:F1} BPM");

        _output.WriteLine(
            $"Detected tempo: {result?.Bpm.ToString("F1") ?? "null"}");

        _output.WriteLine(
            $"Rhythm strength: " +
            $"{result?.RhythmStrength.ToString("F3") ?? "null"}");

        // Assert: a rhythmic signal should produce an estimate.
        Assert.NotNull(result);

        // Estimated BPM must fall within the detector's range.
        Assert.InRange(result.Bpm, 60.0, 200.0);

        // Rhythm strength is normalized to [0, 1].
        Assert.InRange(result.RhythmStrength, 0.0, 1.0);

        // Accept original, half-time, or double-time BPM.
        // This checks the periodic structure while allowing
        // common tempo-octave ambiguity.
        bool matchesBeat = IsTempoEquivalent(
            expectedBpm,
            result.Bpm,
            toleranceBpm: 6.0);

        Assert.True(
            matchesBeat,
            $"Expected a tempo equivalent to {expectedBpm:F1} " +
            $"BPM, but detected {result.Bpm:F1} BPM.");
    }

    // --------------------------------------------------
    // TEST 4: CANCELLATION
    // --------------------------------------------------

    [Fact]
    public void EstimateTempo_CanceledToken_Throws()
    {
        float[] samples = TestAudioFactory.GenerateBeatPulses(
            bpm: 120,
            durationSeconds: TestDurationSeconds,
            sampleRate: SampleRate);

        using var file = new TemporaryWavFile(
            samples, SampleRate);

        using var cache = new TestDirectory();
        var service = new AudioAnalysisService(cache.Path);

        using var cancellation = new CancellationTokenSource();

        // Cancel before analysis begins.
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            service.EstimateTempo(
                file.FilePath,
                cancellation.Token));
    }

    // --------------------------------------------------
    // TEST 5: REPEATED ESTIMATION
    // --------------------------------------------------

    [Fact]
    public void EstimateTempo_RepeatedCalls_AreConsistent()
    {
        float[] samples = TestAudioFactory.GenerateBeatPulses(
            bpm: 120,
            durationSeconds: TestDurationSeconds,
            sampleRate: SampleRate);

        using var file = new TemporaryWavFile(
            samples, SampleRate);

        using var cache = new TestDirectory();
        var service = new AudioAnalysisService(cache.Path);

        // The first call performs analysis.
        TempoEstimate? first = service.EstimateTempo(
            file.FilePath,
            CancellationToken.None);

        // The second call may load the cached estimate.
        TempoEstimate? second = service.EstimateTempo(
            file.FilePath,
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.Equal(first.Bpm, second.Bpm);
        Assert.Equal(
            first.RhythmStrength,
            second.RhythmStrength);
    }

    // ==================================================
    // HELPER: TEMPO OCTAVE EQUIVALENCE
    // ==================================================

    /// <summary>
    /// Compare the measured tempo with the expected beat
    /// rate and its common half/double-time alternatives.
    ///
    /// Examples:
    /// 120 BPM is rhythmically related to 60 and 240 BPM.
    /// 160 BPM is rhythmically related to 80 BPM.
    /// </summary>
    private static bool IsTempoEquivalent(
        double expectedBpm,
        double actualBpm,
        double toleranceBpm)
    {
        double[] candidates =
        {
            expectedBpm / 2.0,
            expectedBpm,
            expectedBpm * 2.0
        };

        foreach (double candidate in candidates)
        {
            if (Math.Abs(actualBpm - candidate) <= toleranceBpm)
                return true;
        }

        return false;
    }
}
