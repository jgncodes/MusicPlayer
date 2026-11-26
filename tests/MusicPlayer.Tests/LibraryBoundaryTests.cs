using System;
using System.IO;
using System.Linq;

namespace MusicPlayer.Tests;

public class LibraryBoundaryTests
{
    [Fact]
    public void LargeFolderPreservesDistinctNamesAndSearchTreatsPunctuationLiterally()
    {
        using var folder = new TestDirectory();
        string[] names = Enumerable.Range(0, 256).Reverse().Select(i => $"Été [{i:D3}].MP3")
            .Append("Été [002].wav").ToArray();
        foreach (string name in names)
            File.WriteAllBytes(Path.Combine(folder.Path, name), []);
        var discovered = MusicLibraryLogic.GetAudioFilePaths(folder.Path)
            .Select(path => Path.GetFileName(path)!).ToArray();

        Assert.Equal(names.Order(StringComparer.OrdinalIgnoreCase), discovered);
        Assert.Equal(new[] { "Été [002].MP3", "Été [002].wav" },
            discovered.Where(name => MusicLibraryLogic.MatchesSearch(name, "  ÉTÉ [002] ")));
        Assert.All(discovered, name => Assert.True(MusicLibraryLogic.MatchesSearch(name, " \t ")));
    }

    [Fact]
    public void MissingFolderReportsDiscoveryFailure()
    {
        using var root = new TestDirectory();
        Assert.Throws<DirectoryNotFoundException>(() =>
            MusicLibraryLogic.GetAudioFilePaths(Path.Combine(root.Path, "missing")));
    }
}
