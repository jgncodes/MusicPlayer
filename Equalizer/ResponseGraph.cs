using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MusicPlayer;

public sealed class ResponseGraph : FrameworkElement
{
    private const double Left = 50, Top = 18, Right = 25, Bottom = 31;
    private static readonly Pen GridPen = MakePen("#2B3A4C", 1), ZeroPen = MakePen("#647B92", 1),
        CurvePen = MakePen("#82D4FF", 2.4), OverlayPen = MakePen("#FFD18F", 1.7, true),
        SelectedPen = MakePen("#FFFFFF", 2), MarkerPen = MakePen("#9ADAFB", 1.5);
    private static readonly Brush Surface = MakeBrush("#141D29"), Ink = MakeBrush("#B3C5D7"),
        MarkerFill = MakeBrush("#245776");
    private static readonly Typeface Font = new("Segoe UI");
    private static readonly double[] TicksHz = [5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000, 200000];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private EqualizerEditor? _editor;
    private EqualizerResponse? _response;
    private ResponseOverlay _overlay;
    private DrawingGroup? _background;
    private PlotScale _scale, _dragScale;
    private int _dragId;
    private ParametricBand _original;
    private Point _origin;
    private ParametricBand? _pending;
    internal event Action<string>? EditRejected;

    public ResponseGraph()
    {
        Focusable = true;
        ClipToBounds = true;
        _timer.Tick += (_, _) => { _timer.Stop(); Flush(); };
        Unloaded += (_, _) => EndGesture();
    }

    internal void SetTarget(EqualizerEditor editor, EqualizerResponse response, ResponseOverlay overlay)
    {
        _editor = editor;
        _response = response;
        _overlay = overlay;
        _background = null;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _background = null;
        EndGesture();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _background = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double width = ActualWidth - Left - Right, height = ActualHeight - Top - Bottom;
        drawing.DrawRectangle(Surface, null, new Rect(RenderSize));
        if (_editor == null || _response == null || width < 1 || height < 1)
            return;
        if (ParametricEqualizerSettings.MaximumHz(_response.SampleRate) <= ParametricEqualizerSettings.MinimumHz(_response.SampleRate))
            return;
        _background ??= BuildBackground(width, height);
        drawing.DrawDrawing(_background);
        if (_editor.Mode != EqualizerMode.Parametric)
            return;
        for (int i = 0; i < _editor.Bands.Count; i++)
            if (i != _editor.SelectedIndex)
                DrawMarker(drawing, i, false);
        if (_editor.SelectedIndex >= 0)
            DrawMarker(drawing, _editor.SelectedIndex, true);
    }

    private DrawingGroup BuildBackground(double width, double height)
    {
        var editor = _editor!;
        var response = _response!;
        var bands = editor.Mode == EqualizerMode.Parametric ? editor.Bands : Array.Empty<ParametricBand>();
        var plot = ResponsePlot.Sample(response, bands, editor.SelectedIndex, _overlay, (int)(width * 0.8));
        _scale = new(plot.Frequencies[0], plot.Frequencies[^1], plot.LowDb, plot.HighDb, width, height);
        var background = new DrawingGroup();
        using (var dc = background.Open())
        {
            double lastLabel = -100;
            foreach (double frequency in TicksHz)
            {
                if (frequency < _scale.LowHz || frequency > _scale.HighHz)
                    continue;
                double x = Left + _scale.X(frequency);
                dc.DrawLine(GridPen, new(x, Top), new(x, Top + height));
                if (x - lastLabel < 32)
                    continue;
                string label = frequency >= 1000 ? $"{frequency / 1000:G}k" : $"{frequency:G}";
                Text(dc, label, x, Top + height + 5, centered: true);
                lastLabel = x;
            }
            int intervals = Math.Clamp((int)(height / 20), 1, 8);
            double step = 6 * Math.Max(1, Math.Ceiling((plot.HighDb - plot.LowDb) / (6 * intervals)));
            for (double db = 0; db <= plot.HighDb; db += step)
                DrawDbTick(dc, db);
            for (double db = -step; db >= plot.LowDb; db -= step)
                DrawDbTick(dc, db);
            dc.DrawGeometry(null, CurvePen, Line(plot.Frequencies, plot.Filters));
            if (plot.Overlay is { } overlay)
                dc.DrawGeometry(null, OverlayPen, Line(plot.Frequencies, overlay));
            if (plot.Silent)
                Text(dc, "Output silent", Left + 8, Top + 2);
        }
        background.Freeze();
        return background;
    }

    private void DrawDbTick(DrawingContext dc, double db)
    {
        double y = Top + _scale.Y(db);
        dc.DrawLine(db == 0 ? ZeroPen : GridPen, new(Left, y), new(Left + _scale.Width, y));
        Text(dc, $"{db:+0;-0;0}", Left / 2, y - 8, centered: true);
    }

    private StreamGeometry Line(double[] hz, double[] db)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(Position(hz[0], db[0]), false, false);
            for (int i = 1; i < hz.Length; i++)
                path.LineTo(Position(hz[i], db[i]), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private Point Position(double hz, double db) => new(Left + _scale.X(hz), Top + _scale.Y(db));

    private void DrawMarker(DrawingContext dc, int index, bool selected)
    {
        var editor = _editor!;
        int id = editor.Ids[index];
        var band = id == _dragId && _pending.HasValue ? _pending.Value : editor.Bands[index];
        if (!band.Enabled || !ParametricEqualizerSettings.IsAvailable(band, editor.Rate))
            return;
        var p = Position(band.FrequencyHz, band.GainDb);
        var pen = selected ? SelectedPen : MarkerPen;
        if (selected)
            dc.DrawEllipse(null, MarkerPen, p, 13, 13);
        if (band.Type == ParametricFilterType.Peaking)
            dc.DrawEllipse(MarkerFill, pen, p, 8, 8);
        else
        {
            int sign = band.Type == ParametricFilterType.LowShelf ? -1 : 1;
            var shape = new StreamGeometry();
            using (var path = shape.Open())
            {
                path.BeginFigure(new(p.X + 9 * sign, p.Y), true, true);
                path.LineTo(new(p.X - 7 * sign, p.Y - 8), true, false);
                path.LineTo(new(p.X - 7 * sign, p.Y + 8), true, false);
            }
            dc.DrawGeometry(MarkerFill, pen, shape);
        }
        if (selected)
            Text(dc, $"Band {id}", Math.Clamp(p.X - 17, Left, Left + Math.Max(0, _scale.Width - 54)),
                p.Y < Top + 20 ? p.Y + 13 : p.Y - 29);
    }

    private int Hit(Point mouse)
    {
        if (_editor?.Mode != EqualizerMode.Parametric || _scale.Width <= 0)
            return -1;
        int closest = -1;
        double distance = 15 * 15;
        for (int i = 0; i < _editor.Bands.Count; i++)
        {
            var band = _editor.Bands[i];
            if (!band.Enabled || !ParametricEqualizerSettings.IsAvailable(band, _editor.Rate))
                continue;
            double squared = (Position(band.FrequencyHz, band.GainDb) - mouse).LengthSquared;
            if (squared < distance || squared == distance && i == _editor.SelectedIndex)
            {
                closest = i;
                distance = squared;
            }
        }
        return closest;
    }

    internal bool BeginGesture(Point mouse)
    {
        int index = Hit(mouse);
        if (index < 0)
            return false;
        var editor = _editor!;
        editor.Select(editor.Ids[index]);
        _dragId = editor.SelectedId;
        _original = editor.Bands[index];
        _dragScale = _scale;
        _origin = mouse;
        return true;
    }

    internal void MoveGesture(Point mouse)
    {
        if (_dragId == 0)
            return;
        var delta = mouse - _origin;
        double hz = delta.X == 0 ? _original.FrequencyHz : _dragScale.Hz(_dragScale.X(_original.FrequencyHz) + delta.X);
        double db = delta.Y == 0 ? _original.GainDb : Math.Clamp(_dragScale.Db(_dragScale.Y(_original.GainDb) + delta.Y), -24, 24);
        _pending = _original with { FrequencyHz = hz, GainDb = db };
        _timer.Start();
        InvalidateVisual();
    }

    private void Flush()
    {
        if (_pending is not { } band || _editor?.SelectedId != _dragId)
            return;
        _pending = null;
        if (_editor.Replace(band) is { } error)
            EditRejected?.Invoke(error);
    }

    internal void CompleteGesture(Point mouse)
    {
        MoveGesture(mouse);
        Flush();
        EndGesture();
    }

    internal void CancelGesture()
    {
        if (_dragId != 0 && _editor?.SelectedId == _dragId)
        {
            _pending = _original;
            Flush();
        }
        EndGesture();
    }

    internal void EndGesture()
    {
        _timer.Stop();
        _pending = null;
        _dragId = 0;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (!BeginGesture(e.GetPosition(this)))
            return;
        if (!CaptureMouse())
            EndGesture();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragId != 0)
            MoveGesture(p);
        else
        {
            int hit = Hit(p);
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Arrow;
            ToolTip = hit >= 0 ? $"Band {_editor!.Ids[hit]}   {_editor.Bands[hit].FrequencyHz:0.##} Hz   {_editor.Bands[hit].GainDb:+0.##;-0.##;0} dB" : null;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragId == 0)
            return;
        CompleteGesture(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndGesture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dragId != 0)
        {
            CancelGesture();
            e.Handled = true;
        }
        else base.OnKeyDown(e);
    }

    private void Text(DrawingContext dc, string label, double x, double y, bool centered = false)
    {
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Font, 11, Ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(centered ? x - text.Width / 2 : x, y));
    }

    private static SolidColorBrush MakeBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private static Pen MakePen(string color, double width, bool dashed = false)
    {
        var pen = new Pen(MakeBrush(color), width);
        if (dashed) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        return pen;
    }
}
