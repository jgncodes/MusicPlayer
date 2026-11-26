using System;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;

namespace MusicPlayer.Tests;

public class AnalysisRecoveryTests
{
    [Fact]
    public void PreCanceledAnalysisDoesNotOpenMissingFile()
    {
        using var dir = new TestDirectory();
        string missing = Path.Combine(dir.Path, "missing.wav");
        Assert.Throws<OperationCanceledException>(() =>
            new AudioAnalysisService(dir.Path).AnalyzeSignal(missing, new CancellationToken(true)));
    }

    [Fact]
    public void CancelDuringPartialFrameStopsBeforeAnotherRead()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new CancelOnRead(cancellation);
        var error = Assert.Throws<OperationCanceledException>(() =>
            AudioAnalysisService.AnalyzeSignal(source, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, source.Reads);
    }

    private sealed class CancelOnRead(CancellationTokenSource cancellation) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        public int Reads { get; private set; }
        public int Read(Span<float> buffer)
        {
            Reads++;
            cancellation.Cancel();
            buffer[0] = 0.5f;
            return Reads == 1 ? 1 : 0;
        }
    }

    [Fact]
    public void NoiseOverRegularPulsesStillDetectsBeatOrHalfTime()
    {
        using var dir = new TestDirectory();
        float[] beat = TestAudioFactory.GenerateBeatPulses(120, 12);
        float[] noise = TestAudioFactory.GenerateNoise(191, 0.015, durationSeconds: 12);
        for (int i = 0; i < beat.Length; i++)
            beat[i] += noise[i];
        using var wav = new TemporaryWavFile(beat);
        var result = new AudioAnalysisService(dir.Path).EstimateTempo(wav.FilePath, default);
        Assert.NotNull(result);
        Assert.True(Math.Abs(result.Bpm - 120) <= 6 || Math.Abs(result.Bpm - 60) <= 3,
            $"Detected {result.Bpm} BPM");
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CachedWaveformIsReadableWithoutDecoderAndMalformedEntryIsReplaced(bool truncate)
    {
        using var dir = new TestDirectory();
        using var wav = new TemporaryWavFile([0, -0.75f, float.NaN, 2f]);
        var service = new AudioAnalysisService(dir.Path);
        float[] peaks = service.GenerateWaveform(wav.FilePath, default);
        Assert.Equal(0.75f, peaks[100]);
        Assert.Equal(1f, peaks[300]);
        Assert.Equal(2, peaks.Count(x => x > 0));
        string entry = Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "Waveforms")));
        using (File.Open(wav.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(peaks, service.GenerateWaveform(wav.FilePath, default));

        if (truncate)
            File.WriteAllBytes(entry, [1]);
        else
            using (var writer = new BinaryWriter(File.OpenWrite(entry)))
                writer.Write(float.NaN);

        Assert.Equal(peaks, service.GenerateWaveform(wav.FilePath, default));
        using var repaired = new BinaryReader(File.OpenRead(entry));
        Assert.Equal(400 * sizeof(float), repaired.BaseStream.Length);
        Assert.Equal(peaks[0], repaired.ReadSingle());
    }

    [Theory]
    [InlineData(0, 0.4)] [InlineData(201, 0.4)]
    [InlineData(110, double.NaN)] [InlineData(110, 1.1)]
    public void TempoCacheRejectsInvalidValuesAndReusesRepairedEstimate(double bpm, double strength)
    {
        using var dir = new TestDirectory();
        using var wav = new TemporaryWavFile(TestAudioFactory.GenerateBeatPulses(120, 10));
        var service = new AudioAnalysisService(dir.Path);
        var first = service.EstimateTempo(wav.FilePath, default);
        Assert.NotNull(first);
        string entry = Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "Tempo")));
        using (var writer = new BinaryWriter(File.Create(entry)))
        {
            writer.Write(bpm);
            writer.Write(strength);
        }
        Assert.Equal(first, service.EstimateTempo(wav.FilePath, default));
        using (File.Open(wav.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(first, service.EstimateTempo(wav.FilePath, default));
    }

    [Fact]
    public void EditingSameLengthFileInvalidatesWaveformAndTempo()
    {
        using var dir = new TestDirectory();
        float[] beat = TestAudioFactory.GenerateBeatPulses(120, 10);
        using var wav = new TemporaryWavFile(beat);
        using var silence = new TemporaryWavFile(new float[beat.Length]);
        var service = new AudioAnalysisService(dir.Path);
        Assert.True(service.GenerateWaveform(wav.FilePath, default).Max() > 0.5f);
        Assert.NotNull(service.EstimateTempo(wav.FilePath, default));
        var modified = File.GetLastWriteTimeUtc(wav.FilePath).AddMinutes(1);

        File.Copy(silence.FilePath, wav.FilePath, overwrite: true);
        File.SetLastWriteTimeUtc(wav.FilePath, modified);
        Assert.All(service.GenerateWaveform(wav.FilePath, default), peak => Assert.Equal(0f, peak));
        Assert.Null(service.EstimateTempo(wav.FilePath, default));
    }

    [Fact]
    public void LockedCacheEntryIsRecomputedWithoutLeavingTemporaryFiles()
    {
        using var dir = new TestDirectory();
        using var wav = new TemporaryWavFile(TestAudioFactory.GenerateBeatPulses(120, 10));
        var service = new AudioAnalysisService(dir.Path);
        var waveform = service.GenerateWaveform(wav.FilePath, default);
        var tempo = service.EstimateTempo(wav.FilePath, default);
        Assert.NotNull(tempo);
        string waves = Path.Combine(dir.Path, "Waveforms");
        string tempos = Path.Combine(dir.Path, "Tempo");
        using var waveLock = File.Open(Assert.Single(Directory.GetFiles(waves)), FileMode.Open, FileAccess.Read, FileShare.None);
        using var tempoLock = File.Open(Assert.Single(Directory.GetFiles(tempos)), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(waveform, service.GenerateWaveform(wav.FilePath, default));
        Assert.Equal(tempo, service.EstimateTempo(wav.FilePath, default));
        Assert.Empty(Directory.GetFiles(waves, "*.tmp"));
    }

    [Fact]
    public void UnwritableCacheStillReturnsAudioResults()
    {
        using var wav = new TemporaryWavFile(TestAudioFactory.GenerateBeatPulses(120, 10));
        // A regular file cannot contain cache subdirectories, regardless of the user's ACLs.
        var service = new AudioAnalysisService(wav.FilePath);
        Assert.True(service.GenerateWaveform(wav.FilePath, default).Max() > 0.5f);
        Assert.NotNull(service.EstimateTempo(wav.FilePath, default));
    }

    [Fact]
    public void PartialStereoFrameMeasuresBothChannelsAndIgnoresNonfiniteSamples()
    {
        using var wav = new TemporaryWavFile([0.5f, -0.5f, float.NaN, 0], channels: 2);
        var frame = Assert.Single(new AudioAnalysisService().AnalyzeSignal(wav.FilePath, default).Frames);
        Assert.InRange(frame.PeakDbfs, -6.03, -6.01);
        Assert.InRange(frame.RmsDbfs, -9.04, -9.02);
        Assert.Equal(0, frame.Bass + frame.Midrange + frame.Treble);
    }

    [Theory]
    [InlineData(248, 0)] [InlineData(250, 1)]
    [InlineData(3992, 1)] [InlineData(4000, 2)]
    public void BinCenteredTonesAroundBandBoundariesReportDominantBand(int hz, int band)
    {
        const int rate = 8192;
        using var wav = new TemporaryWavFile(TestAudioFactory.GenerateSineWave(hz, 0.5, rate, 0.5), rate);
        var frame = Assert.Single(new AudioAnalysisService().AnalyzeSignal(wav.FilePath, default).Frames);
        double[] power = [frame.Bass, frame.Midrange, frame.Treble];
        Assert.InRange(power[band], 0.8, 1.00001);
        Assert.Equal(1, power.Sum(), 6);
    }

    [Fact]
    public void VeryQuietToneHasFiniteLevelsButNoNormalizedBandEnergy()
    {
        using var wav = new TemporaryWavFile(TestAudioFactory.GenerateSineWave(440, 1e-8, 8192, 0.5), 8192);
        var frame = Assert.Single(new AudioAnalysisService().AnalyzeSignal(wav.FilePath, default).Frames);
        Assert.InRange(frame.PeakDbfs, -160.1, -159.9);
        Assert.Equal(0, frame.Bass + frame.Midrange + frame.Treble);
    }

    [Fact]
    public void LockedFileCanBeAnalyzedAfterRelease()
    {
        using var dir = new TestDirectory();
        using var wav = new TemporaryWavFile([0.5f]);
        var service = new AudioAnalysisService(dir.Path);
        using (File.Open(wav.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => service.AnalyzeSignal(wav.FilePath, default));
            Assert.ThrowsAny<IOException>(() => service.GenerateWaveform(wav.FilePath, default));
            Assert.ThrowsAny<IOException>(() => service.EstimateTempo(wav.FilePath, default));
        }
        Assert.Single(service.AnalyzeSignal(wav.FilePath, default).Frames);
        Assert.Equal(0.5f, service.GenerateWaveform(wav.FilePath, default)[0]);
    }
}
