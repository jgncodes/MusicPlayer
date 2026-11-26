using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NAudio.Wave;

namespace MusicPlayer.Tests;

public class WindowIntegrationTests
{
    [Fact]
    public Task FilterTransportAndAnalysisStayInSyncAcrossTrackChanges() => WpfTestThread.Run(async () =>
    {
        using var profile = new TestDirectory();
        using var library = new TestDirectory();
        WriteTrack(library.Path, "Alpha.wav", TestAudioFactory.GenerateSineWave(80, 0.5, 44100, 2));
        WriteTrack(library.Path, "Bravo.wav", TestAudioFactory.GenerateSineWave(8000, 0.25, 44100, 2));
        File.WriteAllText(Path.Combine(profile.Path, "default-folder.txt"), library.Path);
        var devices = new List<RecordingOutput>();
        var errors = new List<string>();
        var player = new AudioPlaybackService(() =>
        {
            var device = new RecordingOutput { CompleteStopImmediately = true };
            devices.Add(device);
            return device;
        }, action => action());
        var window = new MainWindow(player, new AudioAnalysisService(profile.Path), errors.Add);
        var firstAnalysis = window.CurrentSongAnalysis;
        try
        {
            Assert.Equal("Alpha.wav", window.NowPlayingText.Text);
            Assert.Equal("2 songs in current folder", window.CountText.Text);
            Assert.True(window.PlayPauseButton.IsEnabled);
            Assert.True(window.NextButton.IsEnabled);
            // Select another track before the dispatcher can publish the first result.
            window.SongList.SelectedIndex = 1;
            window.WaveformCanvas.Measure(new Size(400, 80));
            window.WaveformCanvas.Arrange(new Rect(0, 0, 400, 80));
            await Task.WhenAll(firstAnalysis, window.CurrentSongAnalysis);

            var bravo = Assert.IsType<SongFile>(window.NowPlayingPanel.DataContext);
            Assert.Equal("Bravo.wav", bravo.Name);
            Assert.Same(bravo, window.SongList.SelectedItem);
            Assert.Equal("Unable to estimate BPM", bravo.TempoInfoText);
            Assert.Empty(window.AnalysisStatus.Text);
            Assert.Empty(window.WaveformStatus.Text);
            Assert.NotNull(window.WaveformPath.Data);
            Assert.InRange(window.TrebleBar.Value, 99, 100);
            Assert.Equal("0:02", window.DurationText.Text);
            Assert.True(window.SeekSlider.IsEnabled);
            window.SeekSlider.Value = 1;
            Assert.Equal(TimeSpan.FromSeconds(1), player.Position);
            Assert.Equal("0:01", window.ElapsedTimeText.Text);
            Assert.Equal(200, window.WaveformPlayhead.X1, 3);
            window.SeekSlider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            window.SeekSlider.Value = 0.5;
            Assert.Equal(TimeSpan.FromSeconds(1), player.Position);
            window.SeekSlider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Assert.Equal(TimeSpan.FromMilliseconds(500), player.Position);

            window.SearchBox.Text = "  ALPHA ";
            Assert.Single(window.SongList.Items.Cast<SongFile>());
            Assert.Null(window.SongList.SelectedItem);
            Assert.Same(bravo, window.NowPlayingPanel.DataContext);
            Assert.False(window.NextButton.IsEnabled);
            window.SearchBox.Clear();
            Assert.Same(bravo, window.SongList.SelectedItem);
            Assert.Equal(2, devices.Count);

            Click(window.PlayPauseButton);
            Assert.Equal(PlaybackState.Playing, devices[^1].PlaybackState);
            Click(window.PlayPauseButton);
            Assert.Equal(PlaybackState.Paused, devices[^1].PlaybackState);
            window.VolumeSlider.Value = 37;
            Click(window.MuteButton);
            Assert.True(player.IsMuted);
            Assert.Equal(0.37, player.Volume, 5);

            Click(window.RepeatButton);
            player.Position = player.Duration!.Value;
            devices[^1].NotifyStopped();
            Assert.Equal(TimeSpan.Zero, player.Position);
            Assert.Equal(PlaybackState.Playing, devices[^1].PlaybackState);
            Assert.Equal(2, devices.Count);

            Click(window.RepeatButton);
            player.Position = player.Duration.Value;
            devices[^1].NotifyStopped();
            Assert.Equal("Play", window.PlayPauseButton.ToolTip);
            Assert.Equal(PlaybackState.Stopped, devices[^1].PlaybackState);

            Click(window.ShuffleButton);
            Click(window.PreviousButton);
            await window.CurrentSongAnalysis;
            Assert.Equal("Alpha.wav", window.NowPlayingText.Text);
            Assert.InRange(window.BassBar.Value, 99, 100);
            Assert.Equal(PlaybackState.Playing, devices[^1].PlaybackState);
            Assert.True(player.IsMuted);
            Assert.True(devices[1].Disposed);
            Assert.Empty(errors);
        }
        finally
        {
            window.Close();
            await Task.WhenAll(firstAnalysis, window.CurrentSongAnalysis);
        }
        Assert.All(devices, device => Assert.True(device.Disposed));
        Assert.Null(player.Duration);
    });

    [Fact]
    public Task DamagedTrackDisablesPlaybackAndSelectingValidTrackRecovers() => WpfTestThread.Run(async () =>
    {
        using var profile = new TestDirectory();
        using var library = new TestDirectory();
        File.WriteAllText(Path.Combine(library.Path, "A.wav"), "not audio");
        WriteTrack(library.Path, "B.wav", new float[44100]);
        File.WriteAllText(Path.Combine(profile.Path, "default-folder.txt"), library.Path);
        var errors = new List<string>();
        var player = new AudioPlaybackService(() => new RecordingOutput(), a => a());
        var window = new MainWindow(player, new AudioAnalysisService(profile.Path), errors.Add);
        try
        {
            await window.CurrentSongAnalysis;
            Assert.StartsWith("Playback error", Assert.Single(errors));
            Assert.False(window.PlayPauseButton.IsEnabled);
            Assert.False(window.SeekSlider.IsEnabled);
            Assert.Equal("Waveform unavailable", window.WaveformStatus.Text);
            Assert.Equal("Audio analysis unavailable", window.AnalysisStatus.Text);
            window.SongList.SelectedIndex = 1;
            await window.CurrentSongAnalysis;
            Assert.True(window.PlayPauseButton.IsEnabled);
            Assert.True(window.SeekSlider.IsEnabled);
            Assert.Empty(window.AnalysisStatus.Text);
            Assert.Equal("0%", window.BassText.Text);
            Assert.Contains("dBFS", window.RmsText.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ClosingDuringAnalysisReleasesTrackWithoutPublishingLateResults() => WpfTestThread.Run(async () =>
    {
        using var profile = new TestDirectory();
        using var library = new TestDirectory();
        WriteTrack(library.Path, "closing.wav", TestAudioFactory.GenerateBeatPulses(120, 10));
        File.WriteAllText(Path.Combine(profile.Path, "default-folder.txt"), library.Path);
        var player = new AudioPlaybackService(() => new RecordingOutput(), action => action());
        var window = new MainWindow(player, new AudioAnalysisService(profile.Path));
        var song = Assert.IsType<SongFile>(window.SongList.SelectedItem);
        string tempoAtClose = song.TempoInfoText;
        var waveformAtClose = window.WaveformPath.Data;
        int changes = 0;
        song.PropertyChanged += (_, _) => changes++;

        window.Close();
        await window.CurrentSongAnalysis;
        Assert.Null(player.Duration);
        Assert.Equal(0, changes);
        Assert.Equal(tempoAtClose, song.TempoInfoText);
        Assert.Same(waveformAtClose, window.WaveformPath.Data);
        using var reopened = File.Open(song.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(reopened.Length > 0);
    });

    [Fact]
    public Task EqualizerPresetsManualGainAndResetUpdateSharedPlayer() => WpfTestThread.Run(() =>
    {
        var player = new AudioPlaybackService();
        player.SetBandGain(3, 4);
        var window = new EqualizerWindow(player);
        try
        {
            Assert.Equal(0, window.PresetBox.SelectedIndex);
            var cards = window.BandsPanel.Children.Cast<Grid>().ToArray();
            Assert.Equal(10, cards.Length);
            Slider Band(int index) => (Slider)((Border)cards[index].Children[1]).Child;
            Assert.Equal(4, Band(3).Value);
            window.PresetBox.SelectedIndex = 4;
            float[] gains = [4, 3, 1, -1, -2, -1, 1, 3, 4, 3];
            Assert.Equal(gains, Enumerable.Range(0, 10).Select(player.GetBandGain));
            Band(8).Value = -8;
            Assert.Equal(-8f, player.GetBandGain(8));
            Assert.Equal(0, window.PresetBox.SelectedIndex);
            var row = (Grid)window.PresetBox.Parent;
            Click(row.Children.OfType<Button>().Single(button => Equals(button.Content, "Reset")));
            Assert.Equal(1, window.PresetBox.SelectedIndex);
            Assert.All(Enumerable.Range(0, 10), i =>
            {
                Assert.Equal(0, Band(i).Value);
                Assert.Equal(0f, player.GetBandGain(i));
            });
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void WriteTrack(string folder, string name, float[] samples)
    {
        using var wav = new TemporaryWavFile(samples);
        File.Copy(wav.FilePath, Path.Combine(folder, name));
    }
}
