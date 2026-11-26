using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MusicPlayer.Tests;

public class SongFileTests
{
    [Theory]
    [InlineData(2, "Stereo")]
    [InlineData(6, "6 channels")]
    public void LibraryDetailsDescribeTheEncodedChannelLayout(int channels, string expectedLayout)
    {
        using var profile = new TestDirectory();
        using var wav = new TemporaryWavFile(new float[8000 * channels], 8000, channels);
        var song = new SongFile(wav.FilePath, new AudioAnalysisService(profile.Path));

        Assert.Equal("0:01", song.LengthText);
        Assert.Contains(expectedLayout, song.AudioDetails);
        Assert.Contains("8 kHz", song.AudioDetails);
        Assert.Equal("BPM not analyzed", song.TempoInfoText);
    }

    [Fact]
    public void DeletedSongRetainsNameAndReportsUnavailableMetadata()
    {
        using var dir = new TestDirectory();
        string path = Path.Combine(dir.Path, "Été [Live].WAV");
        File.WriteAllBytes(path, []);
        var discovered = Assert.Single(MusicLibraryLogic.GetAudioFilePaths(dir.Path));
        File.Delete(path);
        var song = new SongFile(discovered, new AudioAnalysisService(dir.Path));
        Assert.Equal("Été [Live].WAV", song.Name);
        Assert.Equal(path, song.FullPath);
        Assert.Equal("—", song.LengthText);
        Assert.Equal("—", song.BpmText);
        Assert.Equal("WAV", song.AudioDetails);
        Assert.Null(song.Artwork);
        Assert.Equal("BPM not analyzed", song.TempoInfoText);
        var changes = new List<string?>();
        song.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        song.ApplyTempoEstimate(null);
        Assert.Equal("Unable to estimate BPM", song.TempoInfoText);
        Assert.Equal([nameof(song.TempoInfoText)], changes);
        changes.Clear();
        song.ApplyTempoEstimate(new TempoEstimate(119.8, 0.75));
        Assert.Equal("120", song.BpmText);
        Assert.Contains(0.75.ToString("P0"), song.TempoInfoText);
        Assert.Equal([nameof(song.BpmText), nameof(song.TempoInfoText)], changes);
    }

    [Fact]
    public void TaggedBpmIsPreservedWhenEstimateArrives()
    {
        using var dir = new TestDirectory();
        using var wav = new TemporaryWavFile(new float[44100]);
        using (var tags = TagLib.File.Create(wav.FilePath))
        {
            tags.Tag.BeatsPerMinute = 137;
            tags.Save();
        }
        var song = new SongFile(wav.FilePath, new AudioAnalysisService(dir.Path));
        Assert.Equal("0:01", song.LengthText);
        Assert.Contains("Mono", song.AudioDetails);
        var changes = new List<string?>();
        song.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        song.ApplyTempoEstimate(new TempoEstimate(90, 1));
        song.ApplyTempoEstimate(null);
        Assert.Equal("137", song.BpmText);
        Assert.Equal("BPM from file metadata", song.TempoInfoText);
        Assert.Empty(changes);
    }

    [Fact]
    public void ArtworkUsesNormalizedFullPathAndSurvivesClosedInputStream()
    {
        using var dir = new TestDirectory();
        string path = Path.Combine(dir.Path, "a.wav");
        Assert.Equal(SongFile.GetArtworkPath(path), SongFile.GetArtworkPath(path.ToUpperInvariant()));
        Assert.NotEqual(SongFile.GetArtworkPath(path), SongFile.GetArtworkPath(Path.Combine(dir.Path, "nested", "a.wav")));
        var service = new AudioAnalysisService(dir.Path);
        var original = new SongFile(path, service);
        Directory.CreateDirectory(Path.GetDirectoryName(original.ArtworkPath)!);
        File.WriteAllBytes(original.ArtworkPath, [0, 1]);
        Assert.Null(new SongFile(path, service).Artwork);

        var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255 }, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixel));
        using (var stream = File.Create(original.ArtworkPath))
            encoder.Save(stream);
        var song = new SongFile(path, service);
        File.Delete(original.ArtworkPath);
        Assert.NotNull(song.Artwork);
        Assert.True(song.Artwork.IsFrozen);
        Assert.Equal(256, song.Artwork.PixelWidth);
        string? changed = null;
        song.PropertyChanged += (_, e) => changed = e.PropertyName;
        song.Artwork = null;
        Assert.Equal(nameof(song.Artwork), changed);
        Assert.Null(song.Artwork);
    }
}
