using System;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MusicPlayer;

public partial class EqualizerWindow : Window
{
    private readonly AudioPlaybackService _player;

    private readonly Slider[] _sliders =
        new Slider[EqualizerSampleProvider.BandCount];

    private readonly TextBlock[] _valueLabels =
        new TextBlock[EqualizerSampleProvider.BandCount];

    private readonly EqualizerEditor _editor;
    private IReadOnlyList<int>? _displayedIds;
    private bool _refreshing;
    private bool _ready;
    private bool _applyingPreset;

    private static readonly float[][] Presets =
    {
        new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new float[] { 5, 5, 4, 2, 0, 0, 0, 0, 0, 0 },
        new float[] { -2, -1, 0, 1, 2, 3, 3, 1, 0, -1 },
        new float[] { 4, 3, 1, -1, -2, -1, 1, 3, 4, 3 }
    };


    public EqualizerWindow(AudioPlaybackService player)
    {
        _player = player;
        _editor = new EqualizerEditor(player);
        InitializeComponent();
        BuildBandControls();

        BypassBox.Checked += BypassChanged;
        BypassBox.Unchecked += BypassChanged;
        TrimSlider.ValueChanged += (_, e) =>
        {
            if (!_refreshing)
                _player.OutputTrimDb = (float)e.NewValue;
        };
        var peakTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        peakTimer.Tick += (_, _) => RefreshPeakWarning();
        Loaded += (_, _) => { RefreshPeakWarning(); peakTimer.Start(); };
        Closed += (_, _) =>
        {
            _ready = false;
            peakTimer.Stop();
            Graph.EndGesture();
            _editor.Changed -= Refresh;
            _editor.Dispose();
        };
        PeakWarning.Click += (_, _) => { _player.ClearOverload(); RefreshPeakWarning(); };
        Graph.EditRejected += ShowFeedback;
        _editor.Changed += Refresh;
        OverlayBox.SelectedIndex = 0;
        _ready = true;
        Refresh(EqualizerChange.Format);
    }

    private void Refresh(EqualizerChange change)
    {
        if (!_ready)
            return;
        _refreshing = true;
        try
        {
            bool parametric = _editor.Mode == EqualizerMode.Parametric;
            if (change is EqualizerChange.Format or EqualizerChange.Mode or EqualizerChange.Selection ||
                !ReferenceEquals(_displayedIds, _editor.Ids))
                Graph.EndGesture();
            ModeBox.SelectedIndex = parametric ? 1 : 0;
            GraphicPanel.Visibility = parametric ? Visibility.Collapsed : Visibility.Visible;
            ParametricPanel.Visibility = parametric ? Visibility.Visible : Visibility.Collapsed;
            IndividualOption.IsEnabled = parametric && _editor.Selected.HasValue;
            if (!IndividualOption.IsEnabled && (ResponseOverlay)OverlayBox.SelectedIndex == ResponseOverlay.SelectedBand)
                OverlayBox.SelectedIndex = 0;
            BypassBox.IsChecked = !_player.EqualizerEnabled;
            TrimSlider.Value = _player.OutputTrimDb;
            TrimValue.Text = $"{_player.OutputTrimDb:0.#} dB";

            if (change is EqualizerChange.Graphic or EqualizerChange.Format or EqualizerChange.Mode)
            {
                for (int i = 0; i < _sliders.Length; i++)
                    _sliders[i].Value = _player.GetBandGain(i);
                PresetBox.SelectedIndex = Array.FindIndex(Presets, preset =>
                    preset.Select((gain, i) => gain == _player.GetBandGain(i)).All(equal => equal)) + 1;
                UpdateValueLabels();
            }
            if (change is EqualizerChange.Format or EqualizerChange.Mode or EqualizerChange.Bands or EqualizerChange.Selection)
                RefreshFields();
            RefreshGraph();
        }
        finally { _refreshing = false; }
    }

    private void RefreshGraph()
    {
        var response = _editor.Response();
        var overlay = (ResponseOverlay)OverlayBox.SelectedIndex;
        Graph.SetTarget(_editor, response, overlay);
        HeadroomLabel.Text = $"Auto headroom  {response.HeadroomDb:0.0} dB";
        FormatLabel.Text = _editor.IsPreview ? "Frequency (Hz)   ·   48 kHz preview" :
            $"Frequency (Hz)   ·   {_editor.Rate / 1000.0:0.###} kHz track";
        OverlayKey.Text = overlay switch
        {
            ResponseOverlay.Headroom => "┄  With headroom",
            ResponseOverlay.Output => double.IsNegativeInfinity(response.GetDecibels(0, EqualizerResponseStage.Output))
                ? "Output silent" : "┄  Final output",
            ResponseOverlay.SelectedBand => $"┄  Band {_editor.SelectedId}",
            _ => ""
        };
    }

    private void RefreshFields()
    {
        if (!ReferenceEquals(_displayedIds, _editor.Ids))
        {
            _displayedIds = _editor.Ids;
            BandBox.ItemsSource = _displayedIds.Select(id => $"Band {id}").ToArray();
        }
        BandBox.SelectedIndex = _editor.SelectedIndex;
        var selected = _editor.Selected;
        BandBox.IsEnabled = Fields.IsEnabled = RemoveBand.IsEnabled = selected.HasValue;
        TypeBox.IsEnabled = GainBox.IsEnabled = QBox.IsEnabled =
            selected.HasValue && ParametricEqualizerSettings.IsAvailable(selected.Value, _editor.Rate);
        AddBand.IsEnabled = _editor.CanAdd;
        BandCountLabel.Text = $"{_editor.Bands.Count} / {ParametricEqualizerSettings.MaxBands} bands";
        AddBand.ToolTip = _editor.CanAdd ? "Add a flat bell filter" : "All slots are occupied or this format has no usable frequencies.";
        FrequencyBox.Text = selected is { } f ? DisplayNumber(BandParameter.Frequency, f) : "";
        GainBox.Text = selected is { } g ? DisplayNumber(BandParameter.Gain, g) : "";
        QBox.Text = selected is { } q ? DisplayNumber(BandParameter.Q, q) : "";
        BandEnabled.IsChecked = selected?.Enabled ?? false;
        TypeBox.SelectedIndex = selected.HasValue ? (int)selected.Value.Type : -1;
        ShowFeedback(selected switch
        {
            null => "Add a band to shape the response.",
            { } band when !ParametricEqualizerSettings.IsAvailable(band, _editor.Rate) =>
                "Outside this format’s range. Choose an available frequency to edit this filter on this track.",
            { Enabled: false } => "This band is disabled.",
            _ => ""
        });
    }

    private void ShowFeedback(string message) => Feedback.Text = message;
    private void FinishEdit(string? error)
    {
        if (error is null)
            return;
        Refresh(EqualizerChange.Selection);
        ShowFeedback(error);
    }

    private void BypassChanged(object sender, RoutedEventArgs e)
    {
        if (!_refreshing) _player.SetEqualizerEnabled(BypassBox.IsChecked != true);
    }

    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_refreshing)
            _editor.Mode = (EqualizerMode)ModeBox.SelectedIndex;
    }

    private void OverlayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_refreshing)
            RefreshGraph();
    }

    private void BandSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_refreshing && BandBox.SelectedIndex >= 0)
            _editor.Select(_editor.Ids[BandBox.SelectedIndex]);
    }

    private void AddBandClick(object sender, RoutedEventArgs e) => FinishEdit(_editor.Add());
    private void RemoveBandClick(object sender, RoutedEventArgs e) => FinishEdit(_editor.Remove());

    private void TypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshing || TypeBox.SelectedIndex < 0)
            return;
        var type = (ParametricFilterType)TypeBox.SelectedIndex;
        bool reduced = _editor.Selected?.Q > ParametricEqualizerSettings.MaximumQ(type);
        string? error = _editor.ChangeType(type);
        FinishEdit(error);
        if (error is null && reduced)
            ShowFeedback("Q adjusted to the shelf maximum (√0.5).");
    }

    private void BandEnabledClick(object sender, RoutedEventArgs e)
    {
        if (_editor.Selected is { } band)
            FinishEdit(_editor.Replace(band with { Enabled = BandEnabled.IsChecked == true }));
    }

    private void NumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            CommitNumber((TextBox)sender);
        else if (e.Key == Key.Escape)
            Refresh(EqualizerChange.Selection);
        else return;
        e.Handled = true;
    }

    private void NumberLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitNumber((TextBox)sender);

    private static string DisplayNumber(BandParameter parameter, ParametricBand band) => parameter switch
    {
        BandParameter.Frequency => band.FrequencyHz.ToString("0.######"),
        BandParameter.Gain => band.GainDb.ToString("0.######"),
        // The exact shelf limit rounds upward at some decimal precisions.
        BandParameter.Q => band.Q.ToString("R"),
        _ => throw new ArgumentOutOfRangeException(nameof(parameter))
    };

    private void CommitNumber(TextBox field)
    {
        if (!_ready || _refreshing || _editor.Selected is not { } band || _editor.Mode != EqualizerMode.Parametric)
            return;
        var parameter = field == FrequencyBox ? BandParameter.Frequency : field == GainBox ? BandParameter.Gain : BandParameter.Q;
        if (field.Text == DisplayNumber(parameter, band))
            return;
        string? error = _editor.Edit(parameter, field.Text, CultureInfo.CurrentCulture);
        if (error is null)
            field.Text = DisplayNumber(parameter, _editor.Selected!.Value);
        FinishEdit(error);
    }

    private void BuildBandControls()
    {
        for (int band = 0;
             band < EqualizerSampleProvider.BandCount;
             band++)
        {
            int index = band;

            // Main container for each frequency band.
            Grid column = new();

            column.RowDefinitions.Add(
                new RowDefinition { Height = GridLength.Auto });

            column.RowDefinitions.Add(
                new RowDefinition {
                    Height = new GridLength(1, GridUnitType.Star)
                });

            column.RowDefinitions.Add(
                new RowDefinition { Height = GridLength.Auto });

            column.Margin = new Thickness(2, 0, 2, 0);

            // Current gain displayed above the slider.
            TextBlock gainLabel = new()
            {
                Text = "0",
                FontSize = 11,
                Foreground = Brushes.LightSkyBlue,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            };

            Grid.SetRow(gainLabel, 0);

            // Dark card containing the vertical slider.
            Border sliderCard = new()
            {
                Background = new SolidColorBrush(
                    Color.FromRgb(43, 60, 81)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 12, 6, 12),
                Margin = new Thickness(0, 0, 0, 7)
            };

            Slider slider = new()
            {
                Orientation = Orientation.Vertical,
                Minimum = -12,
                Maximum = 12,
                Value = 0,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = (Style)FindResource("EqSliderStyle")
            };

            sliderCard.Child = slider;
            Grid.SetRow(sliderCard, 1);

            // Frequency label below the card.
            float frequency =
                EqualizerSampleProvider.Frequencies[band];

            string frequencyLabel = frequency >= 1000
                ? $"{frequency / 1000:0.#} kHz"
                : $"{frequency:0.#}";

            AutomationProperties.SetName(slider, $"{frequency:G} Hz graphic gain in decibels");
            TextBlock nameLabel = new()
            {
                Text = frequencyLabel,
                FontSize = 10,
                Foreground = new SolidColorBrush(
                    Color.FromRgb(181, 200, 220)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };

            Grid.SetRow(nameLabel, 2);

            // Update the audio filter whenever a slider moves.
            slider.ValueChanged += (_, _) =>
            {
                if (!_ready || _refreshing || _applyingPreset)
                    return;

                float gain = (float)slider.Value;

                _player.SetBandGain(index, gain);

                gainLabel.Text =
                    $"{gain:+0;-0;0}";

                // Manual adjustment means Custom preset.
                PresetBox.SelectedIndex = 0;
            };

            column.Children.Add(gainLabel);
            column.Children.Add(sliderCard);
            column.Children.Add(nameLabel);

            BandsPanel.Children.Add(column);

            _sliders[band] = slider;
            _valueLabels[band] = gainLabel;
        }
    }

    private void RefreshPeakWarning() =>
        PeakWarning.Visibility = _player.OutputOverloaded ? Visibility.Visible : Visibility.Hidden;

    private void UpdateValueLabels()
    {
        for (int i = 0; i < _sliders.Length; i++)
        {
            float value = (float)_sliders[i].Value;

            _valueLabels[i].Text =
                $"{value:+0;-0;0}";
        }
    }

    private void PresetChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_ready || _refreshing || _applyingPreset)
            return;

        int index = PresetBox.SelectedIndex;

        if (index <= 0)
            return;

        ApplyPreset(Presets[index - 1]);
    }

    private void ApplyPreset(float[] gains)
    {
        _applyingPreset = true;

        try
        {
            for (int i = 0; i < _sliders.Length; i++)
            {
                _sliders[i].Value = gains[i];
            }

            _player.SetPreset(gains);
            UpdateValueLabels();
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    private void ResetEq_Click(
        object sender,
        RoutedEventArgs e)
    {
        ApplyPreset(Presets[0]);
        TrimSlider.Value = 0;
        _player.ClearOverload();
        RefreshPeakWarning();
        PresetBox.SelectedIndex = 1;
    }
}
