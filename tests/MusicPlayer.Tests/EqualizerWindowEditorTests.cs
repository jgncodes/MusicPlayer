using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;
using Xunit.Abstractions;

namespace MusicPlayer.Tests;

public class EqualizerWindowEditorTests(ITestOutputHelper output)
{
    [Fact]
    public Task EditorChangesReachDecodedPcmDuringPlaybackAndSeek() => WpfTestThread.Run(() =>
    {
        const int rate = 48000;
        var samples = Enumerable.Range(0, rate).Select(i => (float)(0.25 * Math.Sin(2 * Math.PI * i / 48))).ToArray();
        using var track = new TemporaryWavFile(samples, rate);
        var device = new RecordingOutput { CompleteStopImmediately = true };
        var player = new AudioPlaybackService(() => device, a => a());
        var window = new EqualizerWindow(player);
        try
        {
            player.Open(new Uri(track.FilePath));
            player.Play();
            var rendered = device.Source.ToSampleProvider();
            AssertGain(0);
            window.ModeBox.SelectedIndex = 1;
            Click(window.AddBand);
            window.GainBox.Text = "-9";
            Leave(window.GainBox);
            AssertGain(-9);
            window.BypassBox.IsChecked = true;
            AssertGain(0);
            window.BypassBox.IsChecked = false;
            player.Position = TimeSpan.Zero;
            AssertGain(-9);
            window.ModeBox.SelectedIndex = 0;
            AssertGain(0);
            window.ModeBox.SelectedIndex = 1;
            AssertGain(-9);
            Assert.Equal(PlaybackState.Playing, device.PlaybackState);

            void AssertGain(double expectedDb)
            {
                float[] block = new float[4800];
                Assert.Equal(block.Length, rendered.Read(block));
                // An integral number of periods after the 20 ms transition.
                double ms = block.Skip(2400).Average(s => (double)s * s);
                Assert.Equal(expectedDb, 10 * Math.Log10(ms / (0.25 * 0.25 / 2)), 4);
            }
        }
        finally { window.Close(); player.Close(); }
        Assert.True(device.Disposed);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ControlsEditLiveTrackAndKeepNumericDraftsAcrossLevelChanges() => WpfTestThread.Run(() =>
    {
        using var track = new TemporaryWavFile(new float[512], 16000);
        var player = new AudioPlaybackService(() => new RecordingOutput(), a => a());
        var window = new EqualizerWindow(player);
        using var keys = new HwndSource(new HwndSourceParameters("EQ test input") { WindowStyle = 0, Width = 1, Height = 1 });
        try
        {
            Assert.Equal(0, window.ModeBox.SelectedIndex);
            Assert.Contains("preview", window.FormatLabel.Text);
            window.ModeBox.SelectedIndex = 1;
            Assert.Equal(Visibility.Visible, window.ParametricPanel.Visibility);
            Assert.False(window.Fields.IsEnabled);
            Assert.False(window.RemoveBand.IsEnabled);
            Click(window.AddBand);
            Assert.Single(player.ParametricSettings.Bands);
            Assert.True(window.Fields.IsEnabled);
            player.Open(new Uri(track.FilePath));
            Assert.Contains("16 kHz track", window.FormatLabel.Text);
            window.GainBox.Text = "8";
            player.Volume = 0.3;
            Assert.Equal("8", window.GainBox.Text);
            Assert.Equal(0, player.ParametricSettings.Bands[0].GainDb);
            KeyOn(window.GainBox, Key.Enter);
            Assert.Equal(8, player.GetEqualizerResponse()!.GetDecibels(1000), 6);
            window.QBox.Text = "8";
            Leave(window.QBox);
            window.TypeBox.SelectedIndex = 1;
            Assert.Equal(Math.Sqrt(0.5), player.ParametricSettings.Bands[0].Q);
            Assert.Contains("adjusted", window.Feedback.Text);
            window.FrequencyBox.Text = "8900";
            KeyOn(window.FrequencyBox, Key.Enter);
            Assert.Equal("1000", window.FrequencyBox.Text);
            Assert.Contains("7920", window.Feedback.Text);
            window.GainBox.Text = "invalid";
            Leave(window.GainBox);
            Assert.Contains("finite", window.Feedback.Text);
            window.FrequencyBox.Text = "350";
            KeyOn(window.FrequencyBox, Key.Escape);
            Assert.Equal("1000", window.FrequencyBox.Text);
            window.FrequencyBox.Text = "350";
            Leave(window.FrequencyBox);
            Assert.Equal(350, player.ParametricSettings.Bands[0].FrequencyHz);
            window.TypeBox.SelectedIndex = 2;
            window.BandEnabled.IsChecked = false;
            Click(window.BandEnabled);
            Assert.Equal(0, player.GetEqualizerResponse()!.GetDecibels(350));
            Assert.Contains("disabled", window.Feedback.Text);
            window.BandEnabled.IsChecked = true;
            Click(window.BandEnabled);
            Click(window.AddBand);
            window.BandBox.SelectedIndex = 0;
            Assert.Equal("350", window.FrequencyBox.Text);
            window.OverlayBox.SelectedIndex = (int)ResponseOverlay.SelectedBand;
            Assert.Contains("Band 1", window.OverlayKey.Text);
            window.OverlayBox.SelectedIndex = (int)ResponseOverlay.Output;
            player.IsMuted = true;
            Assert.Contains("silent", window.OverlayKey.Text);
            window.OverlayBox.SelectedIndex = (int)ResponseOverlay.Headroom;
            Assert.Contains("headroom", window.OverlayKey.Text);
            window.BypassBox.IsChecked = true;
            Assert.Equal(0, player.GetEqualizerResponse()!.HeadroomDb);
            window.ModeBox.SelectedIndex = 0;
            window.PresetBox.SelectedIndex = 2;
            Assert.Equal(EqualizerMode.Graphic, player.Mode);
            window.ModeBox.SelectedIndex = 1;
            Assert.Equal("350", window.FrequencyBox.Text);
            Click(window.RemoveBand);
            Click(window.RemoveBand);
            Assert.Empty(player.ParametricSettings.Bands);
            Assert.False(window.RemoveBand.IsEnabled);
            player.Close();
            Assert.Contains("preview", window.FormatLabel.Text);
        }
        finally { window.Close(); player.Close(); }
        player.Mode = EqualizerMode.Graphic;
        Assert.Equal(1, window.ModeBox.SelectedIndex); // closed view was detached
        return Task.CompletedTask;

        void KeyOn(TextBox box, Key key) => box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, keys, 0, key)
            { RoutedEvent = Keyboard.KeyDownEvent });
    });

    [Fact]
    public Task GraphPublishesBoundedDragAndExactEndpointAndCancelsSafely() => WpfTestThread.Run(async () =>
    {
        var player = new AudioPlaybackService { Mode = EqualizerMode.Parametric };
        var window = new EqualizerWindow(player);
        try
        {
            Click(window.AddBand);
            var graph = window.Graph;
            const int width = 700, height = 275;
            var bitmap = DrawGraph(graph, width, height);
            var scale = new PlotScale(5, 23760, -24, 24, width - 75, height - 49);
            Point At(double hz, double db) => new(50 + scale.X(hz), 18 + scale.Y(db));
            Assert.False(graph.BeginGesture(new Point(0, 0)));
            int writes = 0;
            player.EqualizerChanged += change => { if (change == EqualizerChange.Bands) writes++; };
            var original = player.ParametricSettings;
            Assert.True(graph.BeginGesture(At(1000, 0)));
            for (int i = 0; i < 300; i++)
                graph.MoveGesture(At(1000 + i, 6));
            Assert.Same(original, player.ParametricSettings);
            await Dispatcher.Yield(DispatcherPriority.Background);
            // Wait for a real scheduled publication rather than relying on a frame time.
            var timeout = Stopwatch.StartNew();
            while (writes == 0 && timeout.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
            Assert.Equal(1, writes);
            Assert.Equal(1299, player.ParametricSettings.Bands[0].FrequencyHz, 7);
            graph.CompleteGesture(At(1400, -3));
            Assert.Equal(2, writes);
            Assert.Equal(1400, player.ParametricSettings.Bands[0].FrequencyHz, 7);
            Assert.Equal(-3, player.ParametricSettings.Bands[0].GainDb, 7);
            Assert.False(graph.IsMouseCaptured);
            graph.UpdateLayout(); bitmap.Render(graph);
            Assert.True(graph.BeginGesture(At(1400, -3)));
            graph.MoveGesture(At(300, 12));
            graph.CancelGesture();
            Assert.Equal(-3, player.ParametricSettings.Bands[0].GainDb, 7);
            Assert.True(graph.BeginGesture(At(1400, -3)));
            graph.MoveGesture(At(900, 5));
            window.ModeBox.SelectedIndex = 0;
            graph.CompleteGesture(At(400, 15));
            Assert.Equal(2, writes);
            window.ModeBox.SelectedIndex = 1;
            graph.UpdateLayout(); bitmap.Render(graph);
            Assert.True(graph.BeginGesture(At(1400, -3)));
            graph.MoveGesture(At(900, 4));
            window.Close();
            await Task.Delay(50);
            Assert.Equal(2, writes);
        }
        finally { if (window.IsLoaded) window.Close(); }
    });

    [Fact]
    public Task DrawsShelfMarkersAndHandlesEmptyMutedAndResizedGraphs() => WpfTestThread.Run(() =>
    {
        var player = new AudioPlaybackService { Mode = EqualizerMode.Parametric, IsMuted = true };
        var window = new EqualizerWindow(player);
        try
        {
            window.OverlayBox.SelectedIndex = (int)ResponseOverlay.Output;
            DrawGraph(window.Graph, 230, 125);
            player.ParametricSettings = new(new ParametricBand(ParametricFilterType.LowShelf, 100, 18),
                new(ParametricFilterType.HighShelf, 4000, 17), new(ParametricFilterType.Peaking, 300, -24, 10, false));
            DrawGraph(window.Graph, 550, 240);
            window.BandBox.SelectedIndex = 1;
            window.OverlayBox.SelectedIndex = (int)ResponseOverlay.SelectedBand;
            var bitmap = DrawGraph(window.Graph, 990, 400);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            int cyan = 0, amber = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] > 200 && pixels[i + 2] < 170) cyan++;
                if (pixels[i + 2] > 200 && pixels[i] < 200) amber++;
            }
            Assert.True(cyan > 300 && amber > 100);
            window.ModeBox.SelectedIndex = 0;
            Assert.Equal((int)ResponseOverlay.None, window.OverlayBox.SelectedIndex);
            DrawGraph(window.Graph, 40, 24);
            Assert.False(window.Graph.BeginGesture(new Point(15, 15)));
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task MeasureWindowEditsResizeAndModeSwitches() => WpfTestThread.Run(() =>
    {
        using var track = new TemporaryWavFile(new float[1000], 48000);
        var player = new AudioPlaybackService(() => new RecordingOutput(), a => a()) { Mode = EqualizerMode.Parametric };
        var window = new EqualizerWindow(player);
        try
        {
            player.Open(new Uri(track.FilePath));
            for (int i = 0; i < 16; i++) Click(window.AddBand);
            Assert.False(window.AddBand.IsEnabled);
            Assert.Contains("16 / 16", window.BandCountLabel.Text);
            var groups = new ParametricBand[16];
            for (int i = 0; i < groups.Length; i++)
                groups[i] = new(ParametricFilterType.Peaking, 40 * Math.Pow(1.45, i), 8, 16);
            player.ParametricSettings = new(groups);
            window.OverlayBox.SelectedIndex = (int)ResponseOverlay.Output;
            var graph = window.Graph;
            var bitmap = DrawGraph(graph, 760, 270);
            var timings = new double[51];
            for (int i = -5; i < timings.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                window.GainBox.Text = (i % 2 == 0 ? 7 : 8).ToString(CultureInfo.CurrentCulture);
                Leave(window.GainBox);
                graph.Arrange(new Rect(0, 0, i % 2 == 0 ? 760 : 748, 270));
                graph.UpdateLayout();
                bitmap.Render(graph);
                if (i >= 0) timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            Array.Sort(timings);
            output.WriteLine($"16-band edit + alternating width + software bitmap: median {timings[25]:F2} ms, p95 {timings[48]:F2} ms.");
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) window.ModeBox.SelectedIndex = i % 2;
            output.WriteLine($"100 mode publications and synchronous control refreshes: {clock.Elapsed.TotalMilliseconds:F2} ms.");
            Assert.Equal(EqualizerMode.Parametric, player.Mode);
        }
        finally { window.Close(); player.Close(); }
        return Task.CompletedTask;
    });

    private static RenderTargetBitmap DrawGraph(ResponseGraph graph, int width, int height)
    {
        graph.Measure(new Size(width, height));
        graph.Arrange(new Rect(0, 0, width, height));
        graph.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(graph);
        return bitmap;
    }

    private static void Leave(TextBox box) => box.RaiseEvent(new KeyboardFocusChangedEventArgs(
        Keyboard.PrimaryDevice, 0, box, null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
}
