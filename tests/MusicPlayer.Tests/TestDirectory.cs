using System;
using System.IO;

namespace MusicPlayer.Tests;

internal sealed class TestDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("MusicPlayerTests-");
    public string Path => _directory.FullName;
    public void Dispose() => _directory.Delete(recursive: true);
}
