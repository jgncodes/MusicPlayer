using System;

namespace MusicPlayer;

/// <summary>
/// Determines which song should play next.
///
/// Contains no WPF controls or audio hardware dependencies.
/// This makes playback navigation easy to unit test.
///
/// A null result means no song should be selected.
/// </summary>
public static class PlaybackQueue
{
    /// <summary>
    /// Selects the next song index within the visible song list.
    ///
    /// Parameters:
    /// count       - Number of visible songs.
    /// currentIndex - Index of the current song, or -1 if
    ///                it is not present in the visible list.
    /// direction   - 1 for Next, -1 for Previous.
    /// shuffle     - Whether shuffle is enabled.
    /// fromSongEnd - True when called automatically after
    ///               the currently playing song finishes.
    /// random      - Random generator supplied by the caller.
    ///
    /// Returns null if there are no songs or if automatic
    /// playback has reached the end of the visible list.
    /// </summary>
    public static int? GetNextIndex(
        int count,
        int currentIndex,
        int direction,
        bool shuffle,
        bool fromSongEnd,
        Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        if (direction != 1 && direction != -1)
            throw new ArgumentOutOfRangeException(nameof(direction));

        if (currentIndex < -1 || currentIndex >= count)
            throw new ArgumentOutOfRangeException(nameof(currentIndex));

        // No visible songs: playback cannot continue.
        if (count == 0)
            return null;

        // Shuffle selects a different song whenever possible.
        // Preserve the existing behavior of avoiding the
        // current song with two or more visible songs.
        if (shuffle && count > 1)
        {
            int nextIndex;

            do
            {
                nextIndex = random.Next(count);
            }
            while (nextIndex == currentIndex);

            return nextIndex;
        }

        // Automatic playback stops after the final visible
        // song when shuffle is disabled.
        if (fromSongEnd && currentIndex == count - 1)
            return null;

        // No current song in the visible list:
        // Next starts at the first; Previous at the last.
        if (currentIndex == -1)
            return direction > 0 ? 0 : count - 1;

        // Wrap around when navigating manually.
        return (int)(((long)currentIndex + direction + count) % count);
    }
}
