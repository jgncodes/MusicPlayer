using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicPlayer;

/// <summary>
/// Handles music-library file discovery and searching.
///
/// This class does not depend on WPF or an audio device,
/// so its behavior can be tested independently.
/// </summary>
public static class MusicLibraryLogic
{
    /// <summary>
    /// Determines whether a file has an audio extension
    /// supported by MusicPlayer.
    /// </summary>
    public static bool IsSupportedAudioFile(string path)
    {
        string extension = Path.GetExtension(path);

        return extension.Equals(
                   ".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(
                   ".wav", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds supported audio files directly inside a folder.
    ///
    /// Preserves existing MusicPlayer behavior:
    /// - No recursive scanning
    /// - MP3 and WAV only
    /// - Alphabetical sorting, case-insensitive
    /// </summary>
    public static List<string> GetAudioFilePaths(string folder)
    {
        return Directory
            .EnumerateFiles(
                folder,
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(IsSupportedAudioFile)
            .OrderBy(
                path => Path.GetFileName(path),
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Checks whether a song filename matches the search.
    ///
    /// Searching is case-insensitive and leading/trailing
    /// spaces in the search query are ignored.
    /// </summary>
    public static bool MatchesSearch(
        string songName,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(songName);

        string normalizedQuery = (query ?? "").Trim();

        return songName.Contains(
            normalizedQuery,
            StringComparison.OrdinalIgnoreCase);
    }
}
