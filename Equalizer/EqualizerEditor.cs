using System.Globalization;

namespace MusicPlayer;

internal enum BandParameter { Frequency, Gain, Q }

// Control-thread model. Ids identify editor slots even when removing a band
// shifts its DSP index. Audio preferences and accepted values live in the service.
internal sealed class EqualizerEditor : IDisposable
{
    internal const int PreviewRate = 48000;
    private readonly AudioPlaybackService _player;
    private List<int> _ids = [];
    private IReadOnlyList<ParametricBand> _previousBands = [];
    private int _nextId;
    private bool _publishing;
    internal event Action<EqualizerChange>? Changed;

    internal EqualizerEditor(AudioPlaybackService player)
    {
        _player = player;
        AdoptExternalBands();
        player.EqualizerChanged += OnPlayerChanged;
    }

    internal EqualizerMode Mode { get => _player.Mode; set => _player.Mode = value; }
    internal bool IsPreview => _player.DecodedSampleRate is null;
    internal int Rate => _player.DecodedSampleRate ?? PreviewRate;
    internal IReadOnlyList<ParametricBand> Bands => _player.ParametricSettings.Bands;
    internal IReadOnlyList<int> Ids => _ids;
    internal int SelectedId { get; private set; }
    internal int SelectedIndex => _ids.IndexOf(SelectedId);
    internal ParametricBand? Selected => SelectedIndex < 0 ? null : Bands[SelectedIndex];
    internal bool CanAdd => Bands.Count < ParametricEqualizerSettings.MaxBands &&
        ParametricEqualizerSettings.MaximumHz(Rate) >= ParametricEqualizerSettings.MinimumHz(Rate);

    internal EqualizerResponse Response() => _player.GetEqualizerResponse() ?? _player.PreviewResponse(PreviewRate);

    internal void Select(int id)
    {
        if (id == SelectedId || !_ids.Contains(id))
            return;
        SelectedId = id;
        Changed?.Invoke(EqualizerChange.Selection);
    }

    internal string? Add()
    {
        if (!CanAdd)
            return "No band slots or usable frequencies available.";
        double minimum = ParametricEqualizerSettings.MinimumHz(Rate), maximum = ParametricEqualizerSettings.MaximumHz(Rate);
        double frequency = Math.Clamp(1000, minimum, maximum);
        if (Bands.Count > 0)
        {
            var edges = Bands.Where(b => ParametricEqualizerSettings.IsAvailable(b, Rate))
                .Select(b => b.FrequencyHz).Append(minimum).Append(maximum).Order().ToArray();
            int gap = Enumerable.Range(0, edges.Length - 1).MaxBy(i => edges[i + 1] / edges[i]);
            frequency = Math.Clamp(Math.Round(Math.Sqrt(edges[gap] * edges[gap + 1]), 2), minimum, maximum);
        }
        int id = ++_nextId;
        return Publish([.. Bands, new(ParametricFilterType.Peaking, frequency, 0)], [.. _ids, id], id);
    }

    internal string? Remove()
    {
        int index = SelectedIndex;
        if (index < 0)
            return null;
        var bands = Bands.ToList();
        var ids = _ids.ToList();
        bands.RemoveAt(index);
        ids.RemoveAt(index);
        return Publish(bands.ToArray(), ids, ids.Count == 0 ? 0 : ids[Math.Min(index, ids.Count - 1)]);
    }

    internal string? Edit(BandParameter parameter, string text, CultureInfo culture)
    {
        if (!double.TryParse(text, NumberStyles.Float, culture, out double value) || !double.IsFinite(value))
            return "Enter a finite number.";
        if (Selected is not { } band)
            return null;
        return Replace(parameter switch
        {
            BandParameter.Frequency => band with { FrequencyHz = value },
            BandParameter.Gain => band with { GainDb = value },
            BandParameter.Q => band with { Q = value },
            _ => throw new ArgumentOutOfRangeException(nameof(parameter))
        });
    }

    internal string? ChangeType(ParametricFilterType type) => Selected is { } band
        ? Replace(band with { Type = type, Q = Math.Min(band.Q, ParametricEqualizerSettings.MaximumQ(type)) }) : null;

    internal string? Replace(ParametricBand band)
    {
        int index = SelectedIndex;
        if (index < 0 || Bands[index] == band)
            return null;
        if (!ParametricEqualizerSettings.IsAvailable(band, Rate) && band != (Bands[index] with { Enabled = band.Enabled }))
            return $"Enter a frequency from {ParametricEqualizerSettings.MinimumHz(Rate):G} to {ParametricEqualizerSettings.MaximumHz(Rate):G} Hz.";
        var bands = Bands.ToArray();
        bands[index] = band;
        return Publish(bands, _ids, SelectedId);
    }

    private string? Publish(ParametricBand[] bands, List<int> ids, int selection)
    {
        try
        {
            _publishing = true;
            _player.PublishBands(new ParametricEqualizerSettings(bands), retainUnavailable: true);
        }
        catch (ArgumentException error)
        {
            return error.Message.Split(Environment.NewLine)[0].Split(" (Parameter")[0];
        }
        finally { _publishing = false; }
        _ids = ids;
        _previousBands = Bands;
        SelectedId = selection;
        Changed?.Invoke(EqualizerChange.Bands);
        return null;
    }

    private void OnPlayerChanged(EqualizerChange change)
    {
        if (_publishing)
            return;
        if (change == EqualizerChange.Bands)
            AdoptExternalBands();
        Changed?.Invoke(change);
    }

    // Whole-bank replacements have no serialized IDs. Match equal bands first,
    // including duplicates one at a time, then reuse surviving positional slots.
    private void AdoptExternalBands()
    {
        var ids = new int[Bands.Count];
        var used = new bool[_ids.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            for (int j = 0; j < used.Length; j++)
            {
                if (used[j] || Bands[i] != _previousBands[j]) continue;
                ids[i] = _ids[j];
                used[j] = true;
                break;
            }
        }
        for (int i = 0; i < ids.Length; i++)
            if (ids[i] == 0)
                ids[i] = i < used.Length && !used[i] ? _ids[i] : ++_nextId;
        _ids = [.. ids];
        _previousBands = Bands;
        if (!_ids.Contains(SelectedId))
            SelectedId = _ids.FirstOrDefault();
    }

    public void Dispose() => _player.EqualizerChanged -= OnPlayerChanged;
}
