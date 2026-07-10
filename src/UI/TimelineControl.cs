using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Cut.Core;

namespace Cut.UI;

// the whole timeline is one OnRender element: ruler on top, segment bar below.
// kept segments draw as tinted blocks with draggable edge handles; gaps are the cuts.
// ctrl+wheel zooms at the cursor, wheel pans when zoomed.
public sealed class TimelineControl : FrameworkElement
{
    private const double RulerH = 18;
    private const double EdgeHitPx = 6;

    private static readonly Brush BarBg = Frozen(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)));
    private static readonly Brush SegFill = Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x45, 0x24, 0x24)));
    private static readonly Brush SegFillSel = Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0x57, 0x2A, 0x2A)));
    private static readonly Brush EdgeFill = Frozen(new SolidColorBrush(Color.FromRgb(0xC2, 0x2F, 0x2F)));
    private static readonly Brush TickText = Frozen(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)));
    private static readonly Pen SelPen = FrozenPen(Color.FromRgb(0xE0, 0x43, 0x43), 1.5);
    private static readonly Pen TickPen = FrozenPen(Color.FromRgb(0x38, 0x38, 0x38), 1);
    private static readonly Pen PlayheadPen = FrozenPen(Color.FromRgb(0xE0, 0x43, 0x43), 1.5);
    private static readonly Brush PlayheadCap = EdgeFill;
    private static readonly Typeface Face = new("Segoe UI");

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

    private SegmentModel? _model;
    private double _playhead;
    private double _viewStart;
    private double _viewSpan; // <= 0 means full duration

    private enum DragMode { None, Scrub, Edge }
    private DragMode _drag = DragMode.None;
    private int _dragIndex = -1;
    private bool _dragLeftEdge;
    private double _lastScrubX = double.MinValue;

    public int SelectedIndex { get; private set; } = -1;
    public event Action<double, bool>? SeekRequested; // seconds, final (exact seek)
    public event Action<int>? SelectionChanged;

    public TimelineControl()
    {
        Focusable = false;
        ClipToBounds = true;
    }

    public SegmentModel? Model
    {
        get => _model;
        set
        {
            if (_model != null) _model.Changed -= OnModelChanged;
            _model = value;
            if (_model != null) _model.Changed += OnModelChanged;
            _viewSpan = 0;
            Select(-1);
            InvalidateVisual();
        }
    }

    public double Playhead
    {
        get => _playhead;
        set { _playhead = value; InvalidateVisual(); }
    }

    private void OnModelChanged()
    {
        if (_model != null && (SelectedIndex < 0 || SelectedIndex >= _model.Segments.Count))
            Select(Math.Min(SelectedIndex, _model.Segments.Count - 1));
        InvalidateVisual();
    }

    private void Select(int index)
    {
        if (SelectedIndex == index) return;
        SelectedIndex = index;
        SelectionChanged?.Invoke(index);
    }

    private double Duration => _model?.TotalDuration ?? 0;
    private double Span => _viewSpan > 0 ? _viewSpan : Math.Max(Duration, 0.001);
    private double ViewStart => _viewSpan > 0 ? _viewStart : 0;

    private double TimeToX(double t) => (t - ViewStart) / Span * ActualWidth;
    private double XToTime(double x) => ViewStart + x / ActualWidth * Span;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (_model == null || Duration <= 0 || w < 10) return;

        var barTop = RulerH;
        var barH = h - RulerH;
        dc.DrawRoundedRectangle(BarBg, null, new Rect(0, barTop, w, barH), 4, 4);

        DrawRuler(dc, w);

        // kept segments
        for (int i = 0; i < _model.Segments.Count; i++)
        {
            var seg = _model.Segments[i];
            var x1 = Math.Max(0, TimeToX(seg.Start));
            var x2 = Math.Min(w, TimeToX(seg.End));
            if (x2 <= 0 || x1 >= w || x2 - x1 < 1) continue;

            var rect = new Rect(x1, barTop + 3, x2 - x1, barH - 6);
            dc.DrawRoundedRectangle(i == SelectedIndex ? SegFillSel : SegFill,
                i == SelectedIndex ? SelPen : null, rect, 3, 3);

            if (rect.Width > 18)
            {
                dc.DrawRoundedRectangle(EdgeFill, null, new Rect(x1, barTop + 3, 4, barH - 6), 2, 2);
                dc.DrawRoundedRectangle(EdgeFill, null, new Rect(x2 - 4, barTop + 3, 4, barH - 6), 2, 2);
            }
        }

        // playhead
        var px = TimeToX(_playhead);
        if (px >= 0 && px <= w)
        {
            dc.DrawLine(PlayheadPen, new Point(px, barTop - 4), new Point(px, h));
            var cap = new StreamGeometry();
            using (var g = cap.Open())
            {
                g.BeginFigure(new Point(px - 5, barTop - 4), true, true);
                g.LineTo(new Point(px + 5, barTop - 4), false, false);
                g.LineTo(new Point(px, barTop + 3), false, false);
            }
            cap.Freeze();
            dc.DrawGeometry(PlayheadCap, null, cap);
        }
    }

    private void DrawRuler(DrawingContext dc, double w)
    {
        var pxPerSec = w / Span;
        double[] steps = { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };
        var step = steps.FirstOrDefault(s => s * pxPerSec >= 70, steps[^1]);

        var t = Math.Floor(ViewStart / step) * step;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (; t <= ViewStart + Span + step; t += step)
        {
            var x = TimeToX(t);
            if (x < -50 || x > w + 50) continue;
            dc.DrawLine(TickPen, new Point(x, RulerH - 5), new Point(x, RulerH));
            var label = new FormattedText(TimeFmt.Ruler(t), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Face, 10, TickText, dpi);
            dc.DrawText(label, new Point(x + 3, 0));
        }
    }

    // ---- input ----

    private (int index, bool left)? HitEdge(Point p)
    {
        if (_model == null || p.Y < RulerH - 2) return null;
        for (int i = 0; i < _model.Segments.Count; i++)
        {
            var seg = _model.Segments[i];
            if (Math.Abs(p.X - TimeToX(seg.Start)) <= EdgeHitPx) return (i, true);
            if (Math.Abs(p.X - TimeToX(seg.End)) <= EdgeHitPx) return (i, false);
        }
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_model == null || Duration <= 0) return;
        var p = e.GetPosition(this);

        if (HitEdge(p) is { } edge)
        {
            _drag = DragMode.Edge;
            _dragIndex = edge.index;
            _dragLeftEdge = edge.left;
            _model.BeginEdit();
            Select(edge.index);
        }
        else
        {
            _drag = DragMode.Scrub;
            var t = Math.Clamp(XToTime(p.X), 0, Duration);
            Select(_model.IndexAt(t));
            _lastScrubX = p.X;
            SeekRequested?.Invoke(t, false);
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        switch (_drag)
        {
            case DragMode.Edge:
                _model?.MoveEdge(_dragIndex, _dragLeftEdge, Math.Clamp(XToTime(p.X), 0, Duration));
                break;
            case DragMode.Scrub when Math.Abs(p.X - _lastScrubX) > 2:
                _lastScrubX = p.X;
                SeekRequested?.Invoke(Math.Clamp(XToTime(p.X), 0, Duration), false);
                break;
            case DragMode.None:
                Cursor = HitEdge(p) != null ? Cursors.SizeWE : Cursors.Arrow;
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == DragMode.Scrub)
        {
            var t = Math.Clamp(XToTime(e.GetPosition(this).X), 0, Duration);
            SeekRequested?.Invoke(t, true);
        }
        _drag = DragMode.None;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_model == null || Duration <= 0) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var anchor = XToTime(e.GetPosition(this).X);
            var factor = e.Delta > 0 ? 1 / 1.3 : 1.3;
            var span = Math.Clamp(Span * factor, Math.Min(0.5, Duration), Duration);
            var frac = (anchor - ViewStart) / Span;
            _viewStart = Math.Clamp(anchor - frac * span, 0, Math.Max(0, Duration - span));
            _viewSpan = span >= Duration ? 0 : span;
        }
        else if (_viewSpan > 0)
        {
            var shift = Span * 0.1 * (e.Delta > 0 ? -1 : 1);
            _viewStart = Math.Clamp(_viewStart + shift, 0, Math.Max(0, Duration - Span));
        }
        InvalidateVisual();
        e.Handled = true;
    }
}
