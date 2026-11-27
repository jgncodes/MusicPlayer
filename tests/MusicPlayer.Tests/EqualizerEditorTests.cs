using System;
using System.Globalization;
using System.Linq;
using static MusicPlayer.ParametricFilterType;

namespace MusicPlayer.Tests;

public class EqualizerEditorTests
{
    [Fact]
    public void IndependentModesAndStableSlotsSurviveRemovalAndExternalReplacement()
    {
        var player = new AudioPlaybackService();
        player.SetBandGain(4, -8);
        using var editor = new EqualizerEditor(player);
        Assert.Equal(EqualizerMode.Graphic, editor.Mode);
        editor.Mode = EqualizerMode.Parametric;
        Assert.True(editor.IsPreview);
        Assert.Empty(editor.Bands);
        Assert.Null(editor.Selected);
        Assert.Null(editor.Remove());
        Assert.Null(editor.Add());
        var first = editor.SelectedId;
        Assert.Equal(new ParametricBand(Peaking, 1000, 0), editor.Selected);
        Assert.Null(editor.Add());
        var second = editor.SelectedId;
        var original = editor.Selected!.Value;
        Assert.NotEqual(1000, original.FrequencyHz);
        editor.Select(first);
        Assert.Null(editor.Remove());
        Assert.Equal(second, editor.SelectedId);
        Assert.Equal(original, Assert.Single(editor.Bands));
        var snapshot = player.ParametricSettings;
        editor.Mode = EqualizerMode.Graphic;
        Assert.Equal(-8, player.GetBandGain(4));
        editor.Mode = EqualizerMode.Parametric;
        Assert.Same(snapshot, player.ParametricSettings);
        var bass = new ParametricBand(LowShelf, 150, -5);
        player.ParametricSettings = new(bass, original, bass);
        Assert.Equal(second, editor.SelectedId);
        Assert.Equal(1, editor.SelectedIndex);
        Assert.Equal(3, editor.Ids.Distinct().Count());
        editor.Select(int.MaxValue);
        Assert.Equal(second, editor.SelectedId);
        player.ParametricSettings = ParametricEqualizerSettings.Flat;
        Assert.Null(editor.Selected);
        Assert.Equal(0, editor.Response().GetDecibels(400));
    }

    [Theory]
    [InlineData("Frequency", "NaN")]
    [InlineData("Frequency", "Infinity")]
    [InlineData("Frequency", "24000")]
    [InlineData("Frequency", "0")]
    [InlineData("Gain", "-24.1")]
    [InlineData("Gain", "12 dB")]
    [InlineData("Q", "0.24")]
    [InlineData("Q", "16.1")]
    public void InvalidInputLeavesSnapshotAndSelectionIntact(string field, string text)
    {
        var player = new AudioPlaybackService();
        using var editor = new EqualizerEditor(player);
        editor.Add();
        var previous = player.ParametricSettings;
        int selected = editor.SelectedId;
        Assert.NotNull(editor.Edit(Enum.Parse<BandParameter>(field), text, CultureInfo.InvariantCulture));
        Assert.Same(previous, player.ParametricSettings);
        Assert.Equal(selected, editor.SelectedId);
    }

    [Fact]
    public void TypeChangeAppliesShelfLimitAndNumericEditsUseDisplayCulture()
    {
        var player = new AudioPlaybackService();
        using var editor = new EqualizerEditor(player);
        editor.Mode = EqualizerMode.Parametric;
        editor.Add();
        Assert.Null(editor.Edit(BandParameter.Gain, "8,25", CultureInfo.GetCultureInfo("de-DE")));
        Assert.Null(editor.Edit(BandParameter.Q, "15", CultureInfo.InvariantCulture));
        Assert.Null(editor.ChangeType(HighShelf));
        Assert.Equal(Math.Sqrt(0.5), editor.Selected!.Value.Q);
        Assert.Equal(8.25, editor.Selected.Value.GainDb);
        Assert.NotNull(editor.Edit(BandParameter.Q, "0.71", CultureInfo.InvariantCulture));
        Assert.Null(editor.Replace(editor.Selected.Value with { Enabled = false }));
        Assert.Equal(0, editor.Response().GetDecibels(12000));
        Assert.Null(editor.Replace(editor.Selected.Value with { Enabled = true }));
        Assert.True(editor.Response().GetDecibels(12000) > 8);
        while (editor.CanAdd) Assert.Null(editor.Add());
        Assert.Equal(16, editor.Ids.Count);
        Assert.NotNull(editor.Add());
        Assert.Null(editor.Remove());
        Assert.True(editor.CanAdd);
    }

    [Fact]
    public void FormatChangeRetainsUnavailableSlotsAndAdmitsOnlyValidNewEdits()
    {
        using var low = new TemporaryWavFile(new float[160], 16000);
        using var high = new TemporaryWavFile(new float[960], 96000);
        var player = new AudioPlaybackService(() => new RecordingOutput(), a => a());
        var treble = new ParametricBand(HighShelf, 18000, 9);
        var bell = new ParametricBand(Peaking, 500, -6);
        player.ParametricSettings = new(treble, bell);
        using var editor = new EqualizerEditor(player);
        editor.Mode = EqualizerMode.Parametric;
        try
        {
            int first = editor.SelectedId;
            player.Open(new Uri(low.FilePath));
            Assert.False(editor.IsPreview);
            Assert.Equal(16000, editor.Rate);
            Assert.Equal(0, editor.Response().GetBandDecibels(0, 2000));
            Assert.NotNull(editor.Edit(BandParameter.Gain, "3", CultureInfo.InvariantCulture));
            Assert.Null(editor.Replace(treble with { Enabled = false }));
            Assert.False(editor.Selected!.Value.Enabled);
            Assert.Null(editor.Replace(treble));
            editor.Select(editor.Ids[1]);
            Assert.Null(editor.Edit(BandParameter.Frequency, "900", CultureInfo.InvariantCulture));
            Assert.Equal(treble, player.ParametricSettings.Bands[0]);
            Assert.Equal(-6, editor.Response().GetDecibels(900), 7);
            var accepted = player.ParametricSettings;
            Assert.Throws<ArgumentOutOfRangeException>(() => player.PublishBands(new(treble, treble), true));
            Assert.Same(accepted, player.ParametricSettings);
            player.Open(new Uri(high.FilePath));
            editor.Select(first);
            Assert.Equal(9, editor.Response().GetBandDecibels(0, 48000), 7);
            player.Close();
            Assert.True(editor.IsPreview);
            Assert.Equal(EqualizerEditor.PreviewRate, editor.Rate);
        }
        finally { player.Close(); }
    }
}
