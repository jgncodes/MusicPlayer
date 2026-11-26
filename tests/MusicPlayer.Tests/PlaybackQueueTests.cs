using System;
using MusicPlayer;
using Xunit;

namespace MusicPlayer.Tests;

/// <summary>
/// Unit tests for playback queue navigation.
///
/// All tests operate on song indices rather than real
/// audio files, so they run without audio hardware.
/// </summary>
public class PlaybackQueueTests
{
    // -----------------------------------------------
    // TEST 1: NEXT SONG
    // -----------------------------------------------

    [Fact]
    public void NextSong_ReturnsNextIndex()
    {
        int? result = PlaybackQueue.GetNextIndex(
            count: 5,
            currentIndex: 1,
            direction: 1,
            shuffle: false,
            fromSongEnd: false,
            random: new Random(42));

        Assert.Equal(2, result);
    }

    // -----------------------------------------------
    // TEST 2: PREVIOUS SONG
    // -----------------------------------------------

    [Fact]
    public void PreviousSong_ReturnsPreviousIndex()
    {
        int? result = PlaybackQueue.GetNextIndex(
            count: 5,
            currentIndex: 3,
            direction: -1,
            shuffle: false,
            fromSongEnd: false,
            random: new Random(42));

        Assert.Equal(2, result);
    }

    // -----------------------------------------------
    // TEST 3: NEXT WRAPS AROUND
    // -----------------------------------------------

    [Fact]
    public void ManualNext_AtEnd_WrapsToBeginning()
    {
        int? result = PlaybackQueue.GetNextIndex(
            count: 5,
            currentIndex: 4,
            direction: 1,
            shuffle: false,
            fromSongEnd: false,
            random: new Random(42));

        Assert.Equal(0, result);
    }

    // -----------------------------------------------
    // TEST 4: PREVIOUS WRAPS AROUND
    // -----------------------------------------------

    [Fact]
    public void Previous_AtBeginning_WrapsToEnd()
    {
        int? result = PlaybackQueue.GetNextIndex(
            count: 5,
            currentIndex: 0,
            direction: -1,
            shuffle: false,
            fromSongEnd: false,
            random: new Random(42));

        Assert.Equal(4, result);
    }

    // -----------------------------------------------
    // TEST 5: FIRST SELECTION
    // -----------------------------------------------

    [Theory]
    [InlineData(1, 0)]
    [InlineData(-1, 4)]
    public void NoCurrentSong_SelectsBoundary(
        int direction,
        int expectedIndex)
    {
        int? result = PlaybackQueue.GetNextIndex(
            count: 5,
            currentIndex: -1,
            direction: direction,
            shuffle: false,
            fromSongEnd: false,
            random: new Random(42));

        Assert.Equal(expectedIndex, result);
    }

    // -----------------------------------------------
    // TEST 6: EMPTY LIST
    // -----------------------------------------------

    [Fact]
    public void EmptyQueue_ReturnsNull()
    {
        int? result = PlaybackQueue.GetNextIndex(
            count: 0,
            currentIndex: -1,
            direction: 1,
            shuffle: false,
            fromSongEnd: false,
            random: new Random(42));

        Assert.Null(result);
    }

    // -----------------------------------------------
    // TEST 7: AUTOMATIC END OF QUEUE
    // -----------------------------------------------

    [Fact]
    public void SongEnds_AtLastTrack_StopsPlayback()
    {
        int? result = PlaybackQueue.GetNextIndex(
            count: 5,
            currentIndex: 4,
            direction: 1,
            shuffle: false,
            fromSongEnd: true,
            random: new Random(42));

        Assert.Null(result);
    }

    // -----------------------------------------------
    // TEST 8: AUTOMATIC NEXT TRACK
    // -----------------------------------------------

    [Fact]
    public void SongEnds_BeforeLastTrack_Continues()
    {
        int? result = PlaybackQueue.GetNextIndex(
            count: 5,
            currentIndex: 2,
            direction: 1,
            shuffle: false,
            fromSongEnd: true,
            random: new Random(42));

        Assert.Equal(3, result);
    }

    // -----------------------------------------------
    // TEST 9: SHUFFLE AVOIDS CURRENT SONG
    // -----------------------------------------------

    [Fact]
    public void Shuffle_DoesNotSelectCurrentSong()
    {
        var random = new Random(42);

        // Test repeatedly because shuffle produces
        // different results over successive calls.
        for (int i = 0; i < 100; i++)
        {
            int? result = PlaybackQueue.GetNextIndex(
                count: 5,
                currentIndex: 2,
                direction: 1,
                shuffle: true,
                fromSongEnd: false,
                random: random);

            Assert.NotNull(result);
            Assert.InRange(result.Value, 0, 4);
            Assert.NotEqual(2, result.Value);
        }
    }

    // -----------------------------------------------
    // TEST 10: SHUFFLE WITH ONLY TWO SONGS
    // -----------------------------------------------

    [Fact]
    public void Shuffle_TwoSongs_SelectsOtherSong()
    {
        int? result = PlaybackQueue.GetNextIndex(
            count: 2,
            currentIndex: 0,
            direction: 1,
            shuffle: true,
            fromSongEnd: false,
            random: new Random(42));

        Assert.Equal(1, result);
    }

    // -----------------------------------------------
    // TEST 11: SINGLE SONG
    // -----------------------------------------------

    [Fact]
    public void SingleSong_ManualNext_SelectsSameSong()
    {
        int? result = PlaybackQueue.GetNextIndex(
            count: 1,
            currentIndex: 0,
            direction: 1,
            shuffle: true,
            fromSongEnd: false,
            random: new Random(42));

        Assert.Equal(0, result);
    }

    // -----------------------------------------------
    // TEST 12: INVALID DIRECTION
    // -----------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-2)]
    public void InvalidDirection_Throws(int direction)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaybackQueue.GetNextIndex(
                count: 5,
                currentIndex: 0,
                direction: direction,
                shuffle: false,
                fromSongEnd: false,
                random: new Random(42)));
    }

    // -----------------------------------------------
    // TEST 13: INVALID INDEX
    // -----------------------------------------------

    [Theory]
    [InlineData(-2)]
    [InlineData(5)]
    [InlineData(100)]
    public void InvalidCurrentIndex_Throws(int currentIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaybackQueue.GetNextIndex(
                count: 5,
                currentIndex: currentIndex,
                direction: 1,
                shuffle: false,
                fromSongEnd: false,
                random: new Random(42)));
    }

    // -----------------------------------------------
    // TEST 14: INVALID QUEUE SIZE
    // -----------------------------------------------

    [Fact]
    public void ManualNext_LargeQueue_WrapsWithoutIntegerOverflow()
    {
        Assert.Equal(0, PlaybackQueue.GetNextIndex(int.MaxValue, int.MaxValue - 1,
            1, false, false, new Random(1)));
    }

    [Fact]
    public void NegativeQueueSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaybackQueue.GetNextIndex(
                count: -1,
                currentIndex: -1,
                direction: 1,
                shuffle: false,
                fromSongEnd: false,
                random: new Random(42)));
    }

    // -----------------------------------------------
    // TEST 15: SHUFFLE IS REPRODUCIBLE
    // -----------------------------------------------

    [Fact]
    public void Shuffle_SameSeed_ProducesSameSequence()
    {
        var firstRandom = new Random(42);
        var secondRandom = new Random(42);
        int firstIndex = 4, secondIndex = 4;

        for (int i = 0; i < 20; i++)
        {
            int? first = PlaybackQueue.GetNextIndex(
                count: 10,
                currentIndex: firstIndex,
                direction: 1,
                shuffle: true,
                fromSongEnd: false,
                random: firstRandom);

            int? second = PlaybackQueue.GetNextIndex(
                count: 10,
                currentIndex: secondIndex,
                direction: 1,
                shuffle: true,
                fromSongEnd: false,
                random: secondRandom);

            Assert.NotNull(first);
            Assert.NotEqual(firstIndex, first.Value);
            Assert.Equal(first, second);
            firstIndex = first.Value;
            secondIndex = second!.Value;
        }
    }
}
