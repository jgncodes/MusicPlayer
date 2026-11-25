using System;
using System.IO;
using System.Linq;
using MusicPlayer;
using Xunit;

namespace MusicPlayer.Tests;

public class MusicLibraryTests
{
    // TEST 1: SUPPORTED EXTENSIONS

    [Theory]
    [InlineData("song.mp3", true)]
    [InlineData("song.MP3", true)]
    [InlineData("song.wav", true)]
    [InlineData("song.WAV", true)]
    [InlineData("song.flac", false)]
    [InlineData("song.txt", false)]
    [InlineData("song.mp3.txt", false)]
    public void IsSupportedAudioFile_RecognizesExtensions(
        string filename,
        bool expected)
    {
        Assert.Equal(
            expected,
            MusicLibraryLogic.IsSupportedAudioFile(filename));
    }

    // TEST 2: FILTER OUT UNSUPPORTED FILES

    [Fact]
    public void GetAudioFilePaths_OnlyReturnsSupportedFiles()
    {
        using var folder = new TestMusicFolder();

        folder.AddFile("first.mp3");
        folder.AddFile("second.wav");
        folder.AddFile("notes.txt");
        folder.AddFile("cover.jpg");

        var files =
            MusicLibraryLogic.GetAudioFilePaths(folder.Path);

        Assert.Equal(2, files.Count);
        Assert.All(files, file =>
            Assert.True(
                MusicLibraryLogic.IsSupportedAudioFile(file)));
    }

    // TEST 3: ALPHABETICAL ORDER

    [Fact]
    public void GetAudioFilePaths_SortsAlphabetically()
    {
        using var folder = new TestMusicFolder();

        folder.AddFile("zebra.mp3");
        folder.AddFile("Apple.wav");
        folder.AddFile("middle.mp3");

        var names = MusicLibraryLogic
            .GetAudioFilePaths(folder.Path)
            .Select(System.IO.Path.GetFileName)
            .ToArray();

        Assert.Equal(
            new[] { "Apple.wav", "middle.mp3", "zebra.mp3" },
            names);
    }

    // TEST 4: SUBFOLDERS ARE NOT SCANNED

    [Fact]
    public void GetAudioFilePaths_DoesNotSearchSubfolders()
    {
        using var folder = new TestMusicFolder();

        folder.AddFile("main.mp3");

        string subfolder = System.IO.Path.Combine(
            folder.Path, "AnotherAlbum");

        Directory.CreateDirectory(subfolder);
        File.WriteAllBytes(
            System.IO.Path.Combine(subfolder, "hidden.mp3"),
            Array.Empty<byte>());

        var files =
            MusicLibraryLogic.GetAudioFilePaths(folder.Path);

        Assert.Single(files);
        Assert.Equal("main.mp3",
            System.IO.Path.GetFileName(files[0]));
    }

    // TEST 5: SEARCH IS CASE-INSENSITIVE

    [Theory]
    [InlineData("My Favorite Song.mp3", "favorite", true)]
    [InlineData("My Favorite Song.mp3", "FAVORITE", true)]
    [InlineData("My Favorite Song.mp3", "other", false)]
    [InlineData("Jazz.mp3", "", true)]
    [InlineData("Jazz.mp3", "  jazz  ", true)]
    public void MatchesSearch_ReturnsExpectedResult(
        string filename,
        string query,
        bool expected)
    {
        bool result =
            MusicLibraryLogic.MatchesSearch(filename, query);

        Assert.Equal(expected, result);
    }

    // TEST 6: EMPTY LIBRARY

    [Fact]
    public void GetAudioFilePaths_EmptyFolder_ReturnsEmpty()
    {
        using var folder = new TestMusicFolder();

        var files =
            MusicLibraryLogic.GetAudioFilePaths(folder.Path);

        Assert.Empty(files);
    }

    // Shared temporary directory for file-discovery tests.
    private sealed class TestMusicFolder : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"MusicLibraryTest_{Guid.NewGuid():N}");

        public TestMusicFolder()
        {
            Directory.CreateDirectory(Path);
        }

        public void AddFile(string filename)
        {
            File.WriteAllBytes(
                System.IO.Path.Combine(Path, filename),
                Array.Empty<byte>());
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
