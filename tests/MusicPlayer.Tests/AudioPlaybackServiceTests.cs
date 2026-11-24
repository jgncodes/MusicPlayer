using System;
using MusicPlayer;
using Xunit;

namespace MusicPlayer.Tests;

/// <summary>
/// Tests AudioPlaybackService behavior without opening
/// an actual audio device.
///
/// These tests focus on state and configuration.
/// Actual speaker playback requires separate integration tests.
/// </summary>
public class AudioPlaybackServiceTests
{
    // -----------------------------------------------
    // TEST 1: DEFAULT VOLUME
    // -----------------------------------------------

    [Fact]
    public void NewService_DefaultVolumeIsOne()
    {
        var player = new AudioPlaybackService();

        Assert.Equal(1.0, player.Volume);
    }

    // -----------------------------------------------
    // TEST 2: VALID VOLUME VALUES
    // -----------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.50)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    public void Volume_ValidValue_IsPreserved(double value)
    {
        var player = new AudioPlaybackService();

        player.Volume = value;

        Assert.Equal(value, player.Volume);
    }

    // -----------------------------------------------
    // TEST 3: INVALID VOLUME RANGES
    // -----------------------------------------------

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.5, 1.0)]
    [InlineData(100.0, 1.0)]
    public void Volume_OutOfRange_IsClamped(
        double requested,
        double expected)
    {
        var player = new AudioPlaybackService();

        player.Volume = requested;

        Assert.Equal(expected, player.Volume);
    }

    // -----------------------------------------------
    // TEST 4: MUTE STATE
    // -----------------------------------------------

    [Fact]
    public void IsMuted_CanBeEnabledAndDisabled()
    {
        var player = new AudioPlaybackService();

        Assert.False(player.IsMuted);

        player.IsMuted = true;
        Assert.True(player.IsMuted);

        player.IsMuted = false;
        Assert.False(player.IsMuted);
    }

    // -----------------------------------------------
    // TEST 5: DEFAULT EQUALIZER STATE
    // -----------------------------------------------

    [Fact]
    public void NewService_EqualizerStartsFlatAndEnabled()
    {
        var player = new AudioPlaybackService();

        Assert.True(player.EqualizerEnabled);

        for (int i = 0;
             i < EqualizerSampleProvider.BandCount;
             i++)
        {
            Assert.Equal(0f, player.GetBandGain(i));
        }
    }

    // -----------------------------------------------
    // TEST 6: EQUALIZER GAIN VALUES
    // -----------------------------------------------

    [Theory]
    [InlineData(0, 5f)]
    [InlineData(5, -6f)]
    [InlineData(9, 12f)]
    public void SetBandGain_StoresCorrectValue(
        int band,
        float gainDb)
    {
        var player = new AudioPlaybackService();

        player.SetBandGain(band, gainDb);

        Assert.Equal(gainDb, player.GetBandGain(band));
    }

    // -----------------------------------------------
    // TEST 7: EQUALIZER GAIN CLAMPING
    // -----------------------------------------------

    [Theory]
    [InlineData(20f, 12f)]
    [InlineData(-20f, -12f)]
    public void SetBandGain_ClampsInvalidGain(
        float requested,
        float expected)
    {
        var player = new AudioPlaybackService();

        player.SetBandGain(5, requested);

        Assert.Equal(expected, player.GetBandGain(5));
    }

    // -----------------------------------------------
    // TEST 8: INVALID EQUALIZER BAND
    // -----------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public void SetBandGain_InvalidIndexThrows(int band)
    {
        var player = new AudioPlaybackService();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            player.SetBandGain(band, 5f));
    }

    // -----------------------------------------------
    // TEST 9: EQUALIZER RESET
    // -----------------------------------------------

    [Fact]
    public void ResetEqualizer_SetsAllBandsToZero()
    {
        var player = new AudioPlaybackService();

        // Give every band a nonzero value.
        for (int i = 0;
             i < EqualizerSampleProvider.BandCount;
             i++)
        {
            player.SetBandGain(i, 6f);
        }

        // Reset the equalizer.
        player.ResetEqualizer();

        // Every band should now be flat.
        for (int i = 0;
             i < EqualizerSampleProvider.BandCount;
             i++)
        {
            Assert.Equal(0f, player.GetBandGain(i));
        }
    }

    // -----------------------------------------------
    // TEST 10: EQUALIZER ENABLED STATE
    // -----------------------------------------------

    [Fact]
    public void SetEqualizerEnabled_UpdatesState()
    {
        var player = new AudioPlaybackService();

        player.SetEqualizerEnabled(false);
        Assert.False(player.EqualizerEnabled);

        player.SetEqualizerEnabled(true);
        Assert.True(player.EqualizerEnabled);
    }

    // -----------------------------------------------
    // TEST 11: POSITION WITHOUT A SONG
    // -----------------------------------------------

    [Fact]
    public void NoSongLoaded_PositionIsZero()
    {
        var player = new AudioPlaybackService();

        Assert.Equal(TimeSpan.Zero, player.Position);
        Assert.Null(player.Duration);
    }

    // -----------------------------------------------
    // TEST 12: SEEK WITHOUT A SONG
    // -----------------------------------------------

    [Fact]
    public void NoSongLoaded_SettingPositionDoesNotThrow()
    {
        var player = new AudioPlaybackService();

        Exception? exception = Record.Exception(() =>
            player.Position = TimeSpan.FromSeconds(30));

        Assert.Null(exception);
        Assert.Equal(TimeSpan.Zero, player.Position);
    }

    // -----------------------------------------------
    // TEST 13: IDLE PLAYBACK CONTROLS
    // -----------------------------------------------

    [Fact]
    public void NoSongLoaded_PlaybackControlsDoNotThrow()
    {
        var player = new AudioPlaybackService();

        // No audio device should be initialized here.
        Exception? exception = Record.Exception(() =>
        {
            player.Play();
            player.Pause();
            player.Stop();
            player.Close();
        });

        Assert.Null(exception);
    }
}
