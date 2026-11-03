using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;

namespace Cut.UI;

public enum SelKind { None, Segment, Range, Mute, Clip, Lane }

public sealed record Selection(SelKind Kind, int Index = -1, int LaneId = 0, int ClipId = 0, double A = 0, double B = 0)
{
    public static readonly Selection None = new(SelKind.None);
}

// the whole multi-lane timeline as one element with three retained layers:
//   static - ruler, lanes, segments, thumbnails, waveforms, mutes, clips, headers
//   fx     - hover/drag feedback, range selection, snap guide, time bubble
//   head   - the playhead, moved with a transform (playback never redraws the timeline)
// edges are drawn as 1-device-pixel lines exactly on the frame they cut, with a bracket
// pointing into what is kept, so it is never ambiguous where a cut starts or ends.
public sealed class TimelineView : FrameworkElement
{
    public const double HeaderW = 176, RulerH = 26, ClipH = 38, VideoH = 62, AudioH = 52, OverviewH = 12, Gap = 2;
    private const double EdgeHit = 6, SnapPx = 7;

    // ---- palette (frozen) ----
    private static readonly Brush Bg = B(0x0E, 0x0E, 0x10);
    private static readonly Brush RulerBg = B(0x12, 0x12, 0x15);
    private static readonly Brush HeaderBg = B(0x14, 0x14, 0x17);
    private static readonly Brush HeaderSelBg = B(0x1E, 0x1E, 0x23);
    private static readonly Brush LaneBg = B(0x17, 0x17, 0x1B);
    private static readonly Brush LaneSelBg = B(0x1B, 0x1B, 0x20);
    private static readonly Brush KeptBg = B(0x24, 0x26, 0x2B);
    private static readonly Brush GapShade = BA(0xC8, 0x08, 0x08, 0x0A);
    private static readonly Brush TextB = B(0xED, 0xED, 0xEF);
    private static readonly Brush TextDim = B(0x9A, 0x9A, 0xA3);
    private static readonly Brush TextFaint = B(0x6B, 0x6B, 0x74);
    private static readonly Brush Accent = B(0xE5, 0x48, 0x4D);
    private static readonly Brush AccentSoft = BA(0x55, 0xE5, 0x48, 0x4D);
    private static readonly Brush Warn = B(0xE2, 0xA3, 0x3B);
    private static readonly Brush WarnSoft = BA(0x30, 0xE2, 0xA3, 0x3B);
    private static readonly Brush Wave = B(0x7D, 0x85, 0x90);
    private static readonly Brush WaveDim = B(0x4A, 0x4F, 0x57);
    private static readonly Brush MuteFill = BA(0xB0, 0x2A, 0x12, 0x14);
    private static readonly Brush RangeFill = BA(0x22, 0xFF, 0xFF, 0xFF);
    private static readonly Brush ClipFill = B(0x28, 0x2A, 0x31);
    private static readonly Brush ClipSelFill = B(0x3A, 0x1C, 0x1F);
    private static readonly Brush ClipAudioFill = B(0x22, 0x2B, 0x2C);
    private static readonly Brush Bubble = B(0x24, 0x24, 0x2A);
    private static readonly Brush KeyTick = BA(0x90, 0xE2, 0xA3, 0x3B);
    private static readonly Brush OverviewKept = B(0x3A, 0x3D, 0x45);
    private static readonly Brush HatchBrush = Hatch();
    private static readonly Brush CutLine = BA(0x60, 0xE5, 0x48, 0x4D);
    private static readonly Brush Divider = B(0x26, 0x26, 0x2C);
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface IconFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static Brush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    private static Brush BA(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }

    private static Brush Hatch()
    {
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(new Point(0, 8), new Point(8, 0)));
        var d = new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xE5, 0x48, 0x4D)), 1.2), g);
        var brush = new DrawingBrush(d)
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 8, 8), ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    // ---- inputs ----
    public ProjectStore? Store { get; set; }
    public MediaInfo? Media { get; set; }
    public double Duration { get; set; }
    public Func<Lane, Waveform?>? WaveFor { get; set; }
    public Func<Clip, Waveform?>? ClipWaveFor { get; set; }
    public Thumbnails? Thumbs { get; set; }
    public Keyframes? Keys { get; set; }
    public HashSet<int> Solo { get; } = new();
    private bool _fast;
    public bool FastMode
    {
        get => _fast;
        set { _fast = value; RequestStatic(); }
    }

    public Selection Sel { get; private set; } = Selection.None;
    public event Action<double, bool>? SeekRequested;     // time, exact
    public event Action? SelectionChanged;
    public event Action<double>? PlayFrom;
    public event Action? SoloChanged;
    public event Action<Selection, double, Point>? ContextRequested;
    public event Action<string>? Hint;

    // ---- view ----
    private double _viewStart;
    private double _viewSpan;      // <= 0 = whole duration
    private double _scrollY;
    private double _playhead;
    private double _dpi = 1;

    private readonly VisualCollection _children;
    private readonly DrawingVisual _static = new();
    private readonly DrawingVisual _fx = new();
    private readonly DrawingVisual _head = new();
    private readonly TranslateTransform _headX = new();
    private bool _staticDirty, _fxDirty, _hooked;

    private readonly Dictionary<long, BitmapSource> _thumbCache = new();
    private readonly Dictionary<(string, double, bool), FormattedText> _text = new();

    public TimelineView()
    {
        _children = new VisualCollection(this) { _static, _fx, _head };
        _head.Transform = _headX;
        ClipToBounds = true;
        Focusable = false;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => RequestStatic();
        SizeChanged += (_, _) => { RequestStatic(); DrawHead(); };
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    // ---- public api ----

    public double Playhead
    {
        get => _playhead;
        set
        {
            _playhead = value;
            _headX.X = Math.Floor(TimeToX(value) * _dpi) / _dpi - Math.Floor(HeaderW * _dpi) / _dpi;
            _head.Opacity = TimeToX(value) >= HeaderW - 0.5 && TimeToX(value) <= ActualWidth + 0.5 ? 1 : 0;
        }
    }

    public double ViewStart => _viewSpan > 0 ? _viewStart : 0;
    public double Span => _viewSpan > 0 ? _viewSpan : Math.Max(Duration, 0.001);
    private double LaneW => Math.Max(1, ActualWidth - HeaderW);
    public double TimeToX(double t) => HeaderW + (t - ViewStart) / Span * LaneW;
    public double XToTime(double x) => ViewStart + (x - HeaderW) / LaneW * Span;
    private double Fps => Media?.Fps > 1 ? Media.Fps : 0;

    public void Reset()
    {
        _viewSpan = 0;
        _viewStart = 0;
        _scrollY = 0;
        _thumbCache.Clear();
        Sel = Selection.None;
        Solo.Clear();
        RequestStatic();
        DrawHead();
    }

    public void Select(Selection s)
    {
        if (Sel == s) return;
        Sel = s;
        SelectionChanged?.Invoke();
        RequestStatic();
    }

    public void InvalidateContent() => RequestStatic();

    // keep the playhead in view while playing
    public void Follow(double t)
    {
        if (_viewSpan <= 0 || _drag != DragMode.None) return;
        if (t > _viewStart + _viewSpan * 0.97 || t < _viewStart)
        {
            _viewStart = Math.Clamp(t - _viewSpan * 0.08, 0, Math.Max(0, Duration - _viewSpan));
            RequestStatic();
        }
    }

    public void Zoom(double factor, double? anchorT = null)
    {
        if (Duration <= 0) return;
        var anchor = anchorT ?? _playhead;
        var minSpan = Math.Max(Fps > 0 ? 24 / Fps : 0.6, 0.3);
        var span = Math.Clamp(Span * factor, Math.Min(minSpan, Duration), Duration);
        var frac = Math.Clamp((anchor - ViewStart) / Span, 0, 1);
        _viewStart = Math.Clamp(anchor - frac * span, 0, Math.Max(0, Duration - span));
        _viewSpan = span >= Duration - 1e-6 ? 0 : span;
        RequestStatic();
        DrawHead();
    }

    public void Fit()
    {
        _viewSpan = 0;
        _viewStart = 0;
        RequestStatic();
        DrawHead();
    }

    // where a dropped/added clip should go: timeline time and the clip lane under the point
    public (double T, int? LaneId) DropTarget(Point p)
    {
        var t = Math.Clamp(XToTime(p.X), 0, Duration);
        var row = RowAt(p.Y);
        return (t, row?.Kind == RowKind.Clip ? row.Lane!.Id : null);
    }

    // geometry for the demo/guide recorder (element coordinates, dips)
    public Rect VideoLaneRect() => Rows().Where(r => r.Kind == RowKind.Video).Select(r => new Rect(HeaderW, r.Y, LaneW, r.H)).FirstOrDefault();
    public Rect LaneRect(int laneId) => Rows().Where(r => r.Lane?.Id == laneId).Select(r => new Rect(HeaderW, r.Y, LaneW, r.H)).FirstOrDefault();
    public Rect HeaderRect(int laneId) => Rows().Where(r => r.Lane?.Id == laneId).Select(r => new Rect(0, r.Y, HeaderW, r.H)).FirstOrDefault();
    public Rect VolumeBar(int laneId) => Rows().Where(r => r.Lane?.Id == laneId).Select(VolumeRect).FirstOrDefault();
    public double RulerY => RulerH / 2;

    // ---- rows ----

    private enum RowKind { Clip, Video, Audio }
    private sealed record Row(RowKind Kind, Lane? Lane, double Y, double H, int GroupIndex);

    private List<Row> Rows()
    {
        var rows = new List<Row>();
        var s = Store?.State;
        double y = RulerH + 2 - _scrollY;
        if (s != null)
        {
            for (int i = 0; i < s.ClipLanes.Length; i++)
            {
                rows.Add(new Row(RowKind.Clip, s.ClipLanes[i], y, ClipH, i));
                y += ClipH + Gap;
            }
        }
        rows.Add(new Row(RowKind.Video, null, y, VideoH, 0));
        y += VideoH + Gap;
        if (s != null)
        {
            for (int i = 0; i < s.AudioLanes.Length; i++)
            {
                rows.Add(new Row(RowKind.Audio, s.AudioLanes[i], y, AudioH, i));
                y += AudioH + Gap;
            }
        }
        return rows;
    }

    private double ContentHeight()
    {
        var s = Store?.State;
        if (s == null) return VideoH;
        return s.ClipLanes.Length * (ClipH + Gap) + VideoH + Gap + s.AudioLanes.Length * (AudioH + Gap);
    }

    private double LanesBottom => ActualHeight - OverviewH;

    private Row? RowAt(double y) => Rows().FirstOrDefault(r => y >= r.Y && y < r.Y + r.H + Gap);

    // ---- rendering plumbing ----

    private void RequestStatic()
    {
        _staticDirty = true;
        _fxDirty = true;
        Hook();
    }

    private void RequestFx()
    {
        _fxDirty = true;
        Hook();
    }

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _hooked = false;
        if (!IsVisible) return;
        _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_staticDirty)
        {
            _staticDirty = false;
            using var dc = _static.RenderOpen();
            DrawStatic(dc);
        }
        if (_fxDirty)
        {
            _fxDirty = false;
            using var dc = _fx.RenderOpen();
            DrawFx(dc);
        }
        DrawHead();
        Playhead = _playhead;
    }

    // crisp 1-device-pixel vertical line exactly at x
    private void VLine(DrawingContext dc, Brush b, double x, double y0, double y1, double px = 1)
    {
        var w = px / _dpi;
        var sx = Math.Floor(x * _dpi) / _dpi;
        dc.DrawRectangle(b, null, new Rect(sx, y0, w, Math.Max(0, y1 - y0)));
    }

    private void HLine(DrawingContext dc, Brush b, double x0, double x1, double y, double px = 1)
    {
        var h = px / _dpi;
        var sy = Math.Floor(y * _dpi) / _dpi;
        dc.DrawRectangle(b, null, new Rect(x0, sy, Math.Max(0, x1 - x0), h));
    }

    private FormattedText Text(string s, double size, Brush b, bool bold = false)
    {
        var key = (s, size * 1000 + (b.GetHashCode() & 0xFFF), bold);
        if (_text.TryGetValue(key, out var ft)) return ft;
        if (_text.Count > 3000) _text.Clear();
        ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            bold ? FaceBold : Face, size, b, _dpi);
        _text[key] = ft;
        return ft;
    }

    private FormattedText Icon(string glyph, double size, Brush b)
        => new(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconFace, size, b, _dpi);

    // ---- static layer ----

    private void DrawStatic(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        if (Store == null || Duration <= 0 || w < HeaderW + 20)
        {
            var hint = Text("open a video to start", 12, TextFaint);
            dc.DrawText(hint, new Point((w - hint.Width) / 2, (h - hint.Height) / 2));
            return;
        }
        var s = Store.State;
        var rows = Rows();
        var lanesClip = new Rect(HeaderW, RulerH, Math.Max(0, w - HeaderW), Math.Max(0, LanesBottom - RulerH));

        dc.PushClip(new RectangleGeometry(new Rect(0, RulerH, w, Math.Max(0, LanesBottom - RulerH))));
        foreach (var row in rows)
        {
            if (row.Y > LanesBottom || row.Y + row.H < RulerH) continue;
            var selected = Sel.Kind == SelKind.Lane && row.Lane?.Id == Sel.LaneId;
            dc.DrawRectangle(selected ? LaneSelBg : LaneBg, null, new Rect(HeaderW, row.Y, w - HeaderW, row.H));
            dc.PushClip(new RectangleGeometry(lanesClip));
            switch (row.Kind)
            {
                case RowKind.Video: DrawVideoRow(dc, row, s); break;
                case RowKind.Audio: DrawAudioRow(dc, row, s); break;
                case RowKind.Clip: DrawClipRow(dc, row); break;
            }
            dc.Pop();
            DrawHeader(dc, row, s);
        }

        // cut shading through every lane: a cut removes a vertical slice of everything
        dc.PushClip(new RectangleGeometry(lanesClip));
        double prevEnd = 0;
        void Shade(double a, double b)
        {
            if (b - a <= 1e-6) return;
            var x0 = Math.Max(HeaderW, TimeToX(a));
            var x1 = Math.Min(w, TimeToX(b));
            if (x1 <= x0) return;
            foreach (var row in rows.Where(r => r.Kind != RowKind.Video))
                dc.DrawRectangle(GapShade, null, new Rect(x0, row.Y, x1 - x0, row.H));
        }
        foreach (var seg in s.Segments)
        {
            Shade(prevEnd, seg.Start);
            prevEnd = seg.End;
        }
        Shade(prevEnd, Duration);
        // faint cut lines through the other lanes, exactly on each edge
        foreach (var seg in s.Segments)
        {
            foreach (var t in new[] { seg.Start, seg.End })
            {
                if (t <= 0.0005 || t >= Duration - 0.0005) continue;
                var x = TimeToX(t);
                if (x < HeaderW || x > w) continue;
                foreach (var row in rows.Where(r => r.Kind != RowKind.Video))
                    VLine(dc, CutLine, x, row.Y, row.Y + row.H);
            }
        }
        dc.Pop();
        dc.Pop();

        DrawRuler(dc, w);
        DrawOverview(dc, w, s);

        // header column divider
        VLine(dc, Divider, HeaderW - 1, RulerH, LanesBottom);
    }

    private void DrawRuler(DrawingContext dc, double w)
    {
        dc.DrawRectangle(RulerBg, null, new Rect(0, 0, w, RulerH));
        HLine(dc, B(0x26, 0x26, 0x2C), 0, w, RulerH - 1);

        // header corner: kept duration
        if (Store != null)
        {
            var kept = Text($"keeps {TimeFmt.Clock(Store.State.KeptDuration)}", 11, TextDim);
            dc.DrawText(kept, new Point(10, (RulerH - kept.Height) / 2));
        }

        dc.PushClip(new RectangleGeometry(new Rect(HeaderW, 0, Math.Max(0, w - HeaderW), RulerH)));
        var pxPerSec = LaneW / Span;
        double[] steps = { 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
        var step = steps.FirstOrDefault(st => st * pxPerSec >= 80, steps[^1]);
        var minor = step / 5;

        // frame ticks once frames are far enough apart to point at
        if (Fps > 0 && pxPerSec / Fps >= 7)
        {
            var f0 = Math.Floor(ViewStart * Fps);
            for (var f = f0; f / Fps <= ViewStart + Span; f++)
            {
                var x = TimeToX(f / Fps);
                if (x >= HeaderW) VLine(dc, B(0x2E, 0x2E, 0x35), x, RulerH - 4, RulerH - 1);
            }
        }
        var t = Math.Floor(ViewStart / minor) * minor;
        for (; t <= ViewStart + Span + step; t += minor)
        {
            var x = TimeToX(t);
            if (x < HeaderW - 60 || x > w + 60) continue;
            var major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
            if (major)
            {
                VLine(dc, B(0x4A, 0x4A, 0x53), x, RulerH - 9, RulerH - 1);
                var label = Text(TimeFmt.Ruler(t, step), 10.5, TextFaint);
                dc.DrawText(label, new Point(x + 4, 4));
            }
            else
            {
                VLine(dc, B(0x30, 0x30, 0x37), x, RulerH - 5, RulerH - 1);
            }
        }

        // the selected range also shows on the ruler
        if (Sel.Kind == SelKind.Range)
        {
            var x0 = TimeToX(Sel.A);
            var x1 = TimeToX(Sel.B);
            dc.DrawRectangle(BA(0x40, 0xFF, 0xFF, 0xFF), null, new Rect(x0, 0, Math.Max(1, x1 - x0), RulerH - 1));
        }
        dc.Pop();
    }

    private void DrawOverview(DrawingContext dc, double w, ProjectState s)
    {
        var y = LanesBottom;
        dc.DrawRectangle(RulerBg, null, new Rect(0, y, w, OverviewH));
        double OX(double t) => HeaderW + t / Duration * LaneW;
        foreach (var seg in s.Segments)
            dc.DrawRectangle(OverviewKept, null, new Rect(OX(seg.Start), y + 4, Math.Max(1, OX(seg.End) - OX(seg.Start)), OverviewH - 7));
        if (_viewSpan > 0)
        {
            var r = new Rect(OX(_viewStart), y + 1.5, Math.Max(4, OX(_viewStart + _viewSpan) - OX(_viewStart)), OverviewH - 3);
            dc.DrawRoundedRectangle(null, new Pen(B(0x8A, 0x8A, 0x93), 1), r, 2, 2);
        }
        VLine(dc, Accent, OX(_playhead), y + 1, y + OverviewH - 1);
    }

    private void DrawVideoRow(DrawingContext dc, Row row, ProjectState s)
    {
        var top = row.Y + 3;
        var hh = row.H - 6;
        var w = ActualWidth;

        // thumbnails across the whole lane; gaps get shaded after
        if (Thumbs != null && Media != null)
        {
            var slotW = hh * Media.Aspect;
            var spanPerSlot = slotW / LaneW * Span;
            double[] grid = { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200 };
            var q = grid.FirstOrDefault(g => g >= spanPerSlot * 0.9, grid[^1]);
            var first = Math.Floor(ViewStart / q) * q;
            var wanted = new HashSet<long>();
            for (var t = first; t <= ViewStart + Span + q; t += q)
            {
                var x = TimeToX(t);
                if (x + slotW < HeaderW || x > w) continue;
                var key = (long)Math.Round(t * 1000);
                wanted.Add(key);
                var bmp = ThumbBitmap(t);
                if (bmp != null)
                    dc.DrawImage(bmp, new Rect(x, top, slotW, hh));
                else
                {
                    dc.DrawRectangle(KeptBg, null, new Rect(x, top, slotW - 1, hh));
                    Thumbs.Request(t);
                }
            }
            Thumbs.Retain(t => wanted.Contains((long)Math.Round(t * 1000)));
        }

        // cut gaps: dark with red hatch and their length
        double prev = 0;
        var gaps = new List<(double A, double B)>();
        foreach (var seg in s.Segments)
        {
            if (seg.Start - prev > 1e-6) gaps.Add((prev, seg.Start));
            prev = seg.End;
        }
        if (Duration - prev > 1e-6) gaps.Add((prev, Duration));
        foreach (var (a, b) in gaps)
        {
            var x0 = Math.Max(HeaderW, TimeToX(a));
            var x1 = Math.Min(w, TimeToX(b));
            if (x1 <= x0) continue;
            var r = new Rect(x0, top, x1 - x0, hh);
            dc.DrawRectangle(B(0x0C, 0x0C, 0x0E), null, r);
            dc.DrawRectangle(HatchBrush, null, r);
            if (x1 - x0 > 54)
            {
                var label = Text($"cut {TimeFmt.Dur(b - a)}", 10.5, TextDim);
                dc.DrawText(label, new Point(x0 + (x1 - x0 - label.Width) / 2, top + (hh - label.Height) / 2));
            }
        }

        // keyframes, when they matter (fast mode) and are far enough apart to see
        if (FastMode && Keys != null && Span < 180)
        {
            foreach (var k in Keys.InRange(ViewStart, ViewStart + Span))
            {
                var x = TimeToX(k);
                if (x >= HeaderW) dc.DrawRectangle(KeyTick, null, new Rect(Math.Floor(x * _dpi) / _dpi - 1.5, row.Y + row.H - 5, 3, 3));
            }
        }

        // fast mode: where each start really lands (stream copy starts on a keyframe)
        if (FastMode && Keys != null)
        {
            for (int i = 0; i < s.Segments.Length; i++)
            {
                var seg = s.Segments[i];
                var land = Keys.CachedLanding(seg.Start);
                if (land == null)
                {
                    _ = Keys.StartAsync(seg.Start);
                    continue;
                }
                if (seg.Start - land.Value < 0.002) continue;
                var x0 = TimeToX(land.Value);
                var x1 = TimeToX(seg.Start);
                if (x1 < HeaderW || x0 > w) continue;
                var r = new Rect(x0, top, Math.Max(1, x1 - x0), hh);
                dc.DrawRectangle(WarnSoft, null, r);
                Bracket(dc, Warn, land.Value, top, hh, left: true, strong: false);
                if (x1 - x0 > 46)
                {
                    var label = Text($"+{TimeFmt.Dur(seg.Start - land.Value)}", 10, Warn);
                    dc.DrawText(label, new Point(x0 + 6, top + 3));
                }
            }
        }

        // edges: exact brackets, selected segment in accent
        for (int i = 0; i < s.Segments.Length; i++)
        {
            var seg = s.Segments[i];
            var sel = Sel.Kind == SelKind.Segment && Sel.Index == i;
            if (sel)
            {
                var x0 = TimeToX(seg.Start);
                var x1 = TimeToX(seg.End);
                dc.DrawRectangle(BA(0x24, 0xE5, 0x48, 0x4D), null, new Rect(x0, top, Math.Max(0, x1 - x0), hh));
                HLine(dc, Accent, x0, x1, top, 2);
                HLine(dc, Accent, x0, x1, top + hh - 2 / _dpi, 2);
            }
            var brush = sel ? Accent : TextB;
            if (seg.Start > 0.0005 || sel) Bracket(dc, brush, seg.Start, top, hh, left: true, strong: sel);
            if (seg.End < Duration - 0.0005 || sel) Bracket(dc, brush, seg.End, top, hh, left: false, strong: sel);
        }
    }

    // [ for starts, ] for ends: the line sits on the exact cut, the tabs point at what's kept
    private void Bracket(DrawingContext dc, Brush b, double t, double top, double hh, bool left, bool strong)
    {
        var x = TimeToX(t);
        var lw = (strong ? 2 : 1) / _dpi;
        var sx = Math.Floor(x * _dpi) / _dpi;
        var lineX = left ? sx : sx - lw + 1 / _dpi;
        dc.DrawRectangle(b, null, new Rect(lineX, top, lw, hh));
        var tab = 6.0;
        var th = (strong ? 3 : 2) / _dpi;
        var tx = left ? sx : sx - tab + 1 / _dpi;
        dc.DrawRectangle(b, null, new Rect(tx, top, tab, th));
        dc.DrawRectangle(b, null, new Rect(tx, top + hh - th, tab, th));
    }

    private void DrawAudioRow(DrawingContext dc, Row row, ProjectState s)
    {
        var lane = row.Lane!;
        var top = row.Y + 3;
        var hh = row.H - 6;
        var mid = top + hh / 2;
        var w = ActualWidth;
        var audible = PreviewGraph.Audible(lane, Solo);
        var wave = WaveFor?.Invoke(lane);

        if (wave != null && wave.Count > 0)
        {
            var x0 = Math.Max(HeaderW, TimeToX(0));
            var x1 = Math.Min(w, TimeToX(Duration));
            var step = 1.0 / _dpi * 2;
            var geo = new StreamGeometry();
            var amp = hh / 2 - 1;
            var vol = Math.Clamp(lane.Volume, 0, 2);
            using (var g = geo.Open())
            {
                var pts = new List<Point>();
                for (var x = x0; x <= x1; x += step)
                {
                    var t0 = XToTime(x);
                    var t1 = XToTime(x + step);
                    if (!wave.Covers(t0)) break;
                    var p = wave.Peak(t0, t1) / 255.0 * Math.Min(1.15, 0.35 + vol * 0.65);
                    pts.Add(new Point(x, mid - Math.Max(0.5, p * amp)));
                }
                if (pts.Count > 1)
                {
                    g.BeginFigure(new Point(pts[0].X, mid), true, true);
                    g.PolyLineTo(pts, false, false);
                    var back = pts.Select(pt => new Point(pt.X, 2 * mid - pt.Y)).Reverse().ToList();
                    g.PolyLineTo(back, false, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(audible ? Wave : WaveDim, null, geo);
        }
        else
        {
            HLine(dc, WaveDim, HeaderW, w, mid);
        }

        // muted parts
        for (int i = 0; i < lane.Mutes.Length; i++)
        {
            var m = lane.Mutes[i];
            var x0 = TimeToX(m.Start);
            var x1 = TimeToX(m.End);
            if (x1 < HeaderW || x0 > w) continue;
            var r = new Rect(x0, top, Math.Max(1, x1 - x0), hh);
            dc.DrawRectangle(MuteFill, null, r);
            dc.DrawRectangle(HatchBrush, null, r);
            var sel = Sel.Kind == SelKind.Mute && Sel.LaneId == lane.Id && Sel.Index == i;
            VLine(dc, Accent, x0, top, top + hh, sel ? 2 : 1);
            VLine(dc, Accent, x1 - (sel ? 2 : 1) / _dpi, top, top + hh, sel ? 2 : 1);
            if (sel)
            {
                HLine(dc, Accent, x0, x1, top, 2);
                HLine(dc, Accent, x0, x1, top + hh - 2 / _dpi, 2);
            }
            if (x1 - x0 > 50)
            {
                var label = Text("muted", 10.5, B(0xFF, 0xB4, 0xB4));
                dc.DrawText(label, new Point(x0 + 6, top + 3));
            }
        }

        if (!audible) dc.DrawRectangle(BA(0x80, 0x0E, 0x0E, 0x10), null, new Rect(HeaderW, row.Y, w - HeaderW, row.H));
    }

    private void DrawClipRow(DrawingContext dc, Row row)
    {
        var lane = row.Lane!;
        var w = ActualWidth;
        foreach (var c in lane.Clips)
        {
            var x0 = TimeToX(c.Start);
            var x1 = TimeToX(c.End);
            if (x1 < HeaderW || x0 > w) continue;
            var sel = Sel.Kind == SelKind.Clip && Sel.ClipId == c.Id;
            var r = new Rect(x0, row.Y + 3, Math.Max(2, x1 - x0), row.H - 6);
            var fill = sel ? ClipSelFill : c.IsVisual ? ClipFill : ClipAudioFill;
            dc.DrawRoundedRectangle(fill, new Pen(sel ? Accent : B(0x3A, 0x3A, 0x42), sel ? 1.5 : 1), r, 4, 4);

            // mini waveform for anything with sound
            var wave = c.HasAudio ? ClipWaveFor?.Invoke(c) : null;
            if (wave != null && wave.Count > 0)
            {
                var mid = r.Bottom - 8;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    var pts = new List<Point>();
                    var step = 2 / _dpi;
                    for (var x = Math.Max(HeaderW, x0); x <= Math.Min(w, x1); x += step)
                    {
                        var lt = XToTime(x) - c.Start + c.In;
                        var p = wave.Peak(lt, lt + step / LaneW * Span) / 255.0;
                        pts.Add(new Point(x, mid - Math.Max(0.5, p * 6)));
                    }
                    if (pts.Count > 1)
                    {
                        g.BeginFigure(new Point(pts[0].X, mid), true, true);
                        g.PolyLineTo(pts, false, false);
                        g.PolyLineTo(pts.Select(pt => new Point(pt.X, 2 * mid - pt.Y)).Reverse().ToList(), false, false);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(c.MakesSound ? BA(0x90, 0x7D, 0x85, 0x90) : BA(0x50, 0x7D, 0x85, 0x90), null, geo);
            }

            if (r.Width > 30)
            {
                dc.PushClip(new RectangleGeometry(new Rect(r.X + 4, r.Y, Math.Max(0, r.Width - 8), r.Height)));
                var glyph = c.Kind switch
                {
                    ClipKind.Image => "",
                    ClipKind.Gif => "",
                    ClipKind.Video => "",
                    _ => "",
                };
                var icon = Icon(glyph, 11, sel ? Accent : TextDim);
                dc.DrawText(icon, new Point(r.X + 7, r.Y + 6));
                var name = Text(c.Name, 11, TextB);
                dc.DrawText(name, new Point(r.X + 24, r.Y + 5));
                dc.Pop();
            }
            if (!lane.Enabled) dc.DrawRoundedRectangle(BA(0x90, 0x0E, 0x0E, 0x10), null, r, 4, 4);
        }
    }

    // ---- headers ----

    private Rect ToggleRect(Row row) => new(HeaderW - 30, row.Y + 6, 22, 20);
    private Rect SoloRect(Row row) => new(HeaderW - 56, row.Y + 6, 22, 20);
    private Rect VolumeRect(Row row) => new(22, row.Y + row.H - 17, HeaderW - 22 - 52, 12);
    private Rect GripRect(Row row) => new(4, row.Y + 4, 14, row.H - 8);

    private void DrawHeader(DrawingContext dc, Row row, ProjectState s)
    {
        var selected = Sel.Kind == SelKind.Lane && row.Lane?.Id == Sel.LaneId;
        dc.DrawRectangle(selected ? HeaderSelBg : HeaderBg, null, new Rect(0, row.Y, HeaderW - 1, row.H));
        if (selected) dc.DrawRectangle(Accent, null, new Rect(0, row.Y, 2, row.H));

        if (row.Kind == RowKind.Video)
        {
            dc.DrawText(Icon("", 12, TextDim), new Point(10, row.Y + 9));
            dc.DrawText(Text("video", 12, TextB, bold: true), new Point(30, row.Y + 7));
            var cuts = Math.Max(0, s.Segments.Length - 1) + (s.Segments.Length > 0 && s.Segments[0].Start > 0.001 ? 1 : 0)
                       + (s.Segments.Length > 0 && s.Segments[^1].End < Duration - 0.001 ? 1 : 0);
            dc.DrawText(Text(cuts == 0 ? "no cuts" : cuts == 1 ? "1 cut" : $"{cuts} cuts", 11, TextFaint), new Point(30, row.Y + 27));
            if (FastMode)
                dc.DrawText(Text("fast · keyframe starts", 10.5, Warn), new Point(30, row.Y + 42));
            return;
        }

        var lane = row.Lane!;
        // grip dots (drag to reorder)
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 2; j++)
                dc.DrawEllipse(TextFaint, null, new Point(9 + j * 4, row.Y + row.H / 2 - 4 + i * 4), 1, 1);

        var name = row.Kind == RowKind.Clip
            ? (lane.Clips.All(c => !c.IsVisual) ? "audio" : "layer") + $" {s.ClipLanes.Length - row.GroupIndex}"
            : lane.Name;
        var nameText = Text(name, 12, lane.Enabled ? TextB : TextFaint, bold: true);
        dc.PushClip(new RectangleGeometry(new Rect(20, row.Y, HeaderW - 80, row.H)));
        dc.DrawText(nameText, new Point(22, row.Y + 7));
        dc.Pop();

        // solo (preview only) and include/visible toggles
        var solo = Solo.Contains(lane.Id);
        var sr = SoloRect(row);
        dc.DrawRoundedRectangle(solo ? Accent : B(0x24, 0x24, 0x2A), null, sr, 4, 4);
        var st = Text("S", 10.5, solo ? Brushes.White : TextDim, bold: true);
        dc.DrawText(st, new Point(sr.X + (sr.Width - st.Width) / 2, sr.Y + (sr.Height - st.Height) / 2));

        var tr = ToggleRect(row);
        dc.DrawRoundedRectangle(B(0x24, 0x24, 0x2A), null, tr, 4, 4);
        var glyph = row.Kind == RowKind.Clip
            ? (lane.Enabled ? "" : "")
            : (lane.Enabled ? "" : "");
        var gi = Icon(glyph, 11, lane.Enabled ? TextB : Accent);
        dc.DrawText(gi, new Point(tr.X + (tr.Width - gi.Width) / 2, tr.Y + (tr.Height - gi.Height) / 2));

        if (row.Kind == RowKind.Audio)
        {
            // mini fader: drag it, double-click resets
            var vr = VolumeRect(row);
            var mid = vr.Y + vr.Height / 2;
            dc.DrawRoundedRectangle(B(0x2C, 0x2C, 0x33), null, new Rect(vr.X, mid - 2, vr.Width, 4), 2, 2);
            var frac = Math.Clamp(lane.Volume / 2, 0, 1);
            dc.DrawRoundedRectangle(lane.Enabled ? B(0x9A, 0x9A, 0xA3) : TextFaint, null,
                new Rect(vr.X, mid - 2, Math.Max(2, vr.Width * frac), 4), 2, 2);
            VLine(dc, TextFaint, vr.X + vr.Width / 2, mid - 4, mid + 4);
            var vt = Text($"{Math.Round(lane.Volume * 100)}%", 10.5, TextDim);
            dc.DrawText(vt, new Point(HeaderW - 10 - vt.Width, vr.Y + (vr.Height - vt.Height) / 2));
            if (lane.Mutes.Length > 0)
            {
                var mt = Text(lane.Mutes.Length == 1 ? "1 muted part" : $"{lane.Mutes.Length} muted parts", 10, B(0xFF, 0xB4, 0xB4));
                dc.DrawText(mt, new Point(22, row.Y + 22));
            }
        }
    }

    private BitmapSource? ThumbBitmap(double t)
    {
        var key = (long)Math.Round(t * 1000);
        if (_thumbCache.TryGetValue(key, out var b)) return b;
        var th = Thumbs?.Get(t);
        if (th == null) return null;
        var bmp = BitmapSource.Create(th.Width, th.Height, 96, 96, PixelFormats.Bgra32, null, th.Bgra, th.Width * 4);
        bmp.Freeze();
        if (_thumbCache.Count > 1500) _thumbCache.Clear();
        _thumbCache[key] = bmp;
        return bmp;
    }

    // ---- fx layer ----

    private void DrawFx(DrawingContext dc)
    {
        if (Store == null || Duration <= 0) return;
        var w = ActualWidth;
        var rows = Rows();
        dc.PushClip(new RectangleGeometry(new Rect(HeaderW, 0, Math.Max(0, w - HeaderW), LanesBottom)));

        if (Sel.Kind == SelKind.Range)
        {
            var x0 = TimeToX(Sel.A);
            var x1 = TimeToX(Sel.B);
            IEnumerable<Row> target = Sel.LaneId == 0 ? rows : rows.Where(r => (r.Lane?.Id ?? -1) == Sel.LaneId);
            foreach (var r in target)
                dc.DrawRectangle(RangeFill, null, new Rect(x0, r.Y, Math.Max(1, x1 - x0), r.H));
            var yTop = Sel.LaneId == 0 ? RulerH : target.FirstOrDefault()?.Y ?? RulerH;
            var yBot = Sel.LaneId == 0 ? LanesBottom : (target.FirstOrDefault() is { } tr ? tr.Y + tr.H : LanesBottom);
            VLine(dc, TextB, x0, yTop, yBot);
            VLine(dc, TextB, x1 - 1 / _dpi, yTop, yBot);
            DrawBubble(dc, $"{TimeFmt.Precise(Sel.A)} → {TimeFmt.Precise(Sel.B)} · {TimeFmt.Dur(Sel.B - Sel.A)}",
                (x0 + x1) / 2, Math.Max(RulerH + 2, yTop + 2));
        }

        if (_hoverEdgeX is double hx && _drag == DragMode.None)
            VLine(dc, BA(0xC0, 0xFF, 0xFF, 0xFF), hx, _hoverEdgeTop, _hoverEdgeBottom, 2);

        if (_snapX is double sx)
            VLine(dc, BA(0xB0, 0xE2, 0xA3, 0x3B), sx, RulerH, LanesBottom);

        if (_bubble is { } b) DrawBubble(dc, b.Text, b.X, b.Y);

        if (_drag == DragMode.LaneReorder && _reorderY is double ry)
            HLine(dc, Accent, 0, w, ry, 2);
        dc.Pop();

        if (_drag == DragMode.LaneReorder && _reorderY is double ry2)
            HLine(dc, Accent, 0, HeaderW, ry2, 2);
    }

    private void DrawBubble(DrawingContext dc, string text, double cx, double y)
    {
        var ft = Text(text, 11, TextB);
        var bw = ft.Width + 14;
        var x = Math.Clamp(cx - bw / 2, HeaderW + 2, Math.Max(HeaderW + 2, ActualWidth - bw - 2));
        var r = new Rect(x, y, bw, ft.Height + 6);
        dc.DrawRoundedRectangle(Bubble, new Pen(B(0x3A, 0x3A, 0x42), 1), r, 5, 5);
        dc.DrawText(ft, new Point(x + 7, y + 3));
    }

    private void DrawHead()
    {
        using var dc = _head.RenderOpen();
        var x = HeaderW;
        var lw = 1.0 / _dpi;
        var sx = Math.Floor(x * _dpi) / _dpi;
        dc.DrawRectangle(Accent, null, new Rect(sx, 0, Math.Max(lw, 1.5 / _dpi), Math.Max(0, LanesBottom)));
        var cap = new StreamGeometry();
        using (var g = cap.Open())
        {
            g.BeginFigure(new Point(sx - 5.5, 2), true, true);
            g.LineTo(new Point(sx + 6, 2), false, false);
            g.LineTo(new Point(sx + 6, 12), false, false);
            g.LineTo(new Point(sx + 0.25, 18), false, false);
            g.LineTo(new Point(sx - 5.5, 12), false, false);
        }
        cap.Freeze();
        dc.DrawGeometry(Accent, null, cap);
    }

    // ---- input ----

    private enum DragMode { None, Scrub, Overview, SegEdge, MuteEdge, ClipTrim, ClipMove, Range, Volume, LaneReorder, Pending, Pan }

    private DragMode _drag = DragMode.None;
    private Point _down;
    private double _downT;
    private Row? _downRow;
    private int _dragIndex = -1;
    private bool _dragLeft;
    private int _dragLaneId;
    private int _dragClipId;
    private double _grabOffset;
    private Selection _pendingSel = Selection.None;
    private double? _snapX;
    private double? _hoverEdgeX;
    private double _hoverEdgeTop, _hoverEdgeBottom;
    private (string Text, double X, double Y)? _bubble;
    private double? _reorderY;
    private int _reorderTo = -1;
    private DateTime _lastSeek;
    private double _panStartView;

    private double Q(double t) => Fps > 0 ? Math.Round(t * Fps) / Fps : t;

    // snaps to edges/playhead within a few pixels, then to the frame grid
    private double Snap(double t, Func<double, bool>? exclude = null)
    {
        _snapX = null;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || Store == null) return Math.Clamp(Q(t), 0, Duration);
        var best = double.NaN;
        var bestPx = SnapPx;
        void Try(double c)
        {
            if (exclude != null && exclude(c)) return;
            var px = Math.Abs(TimeToX(c) - TimeToX(t));
            if (px < bestPx) { bestPx = px; best = c; }
        }
        Try(_playhead);
        Try(0);
        Try(Duration);
        var s = Store.State;
        foreach (var seg in s.Segments) { Try(seg.Start); Try(seg.End); }
        foreach (var c in s.AllClips) { Try(c.Start); Try(c.End); }
        foreach (var l in s.AudioLanes) foreach (var m in l.Mutes) { Try(m.Start); Try(m.End); }
        if (Sel.Kind == SelKind.Range) { Try(Sel.A); Try(Sel.B); }
        if (!double.IsNaN(best))
        {
            _snapX = TimeToX(best);
            return best;
        }
        return Math.Clamp(Q(t), 0, Duration);
    }

    private enum HitKind { None, Ruler, Overview, Header, Toggle, SoloBtn, Volume, Grip, SegEdge, MuteEdge, ClipEdge, Clip, Mute, Segment, Lane }
    private readonly record struct Hit(HitKind Kind, Row? Row, int Index = -1, bool Left = false, int ClipId = 0);

    private Hit HitTest(Point p)
    {
        if (Store == null || Duration <= 0) return new Hit(HitKind.None, null);
        if (p.Y < RulerH) return new Hit(HitKind.Ruler, null);
        if (p.Y >= LanesBottom) return new Hit(HitKind.Overview, null);
        var row = RowAt(p.Y);
        if (row == null) return new Hit(p.X < HeaderW ? HitKind.None : HitKind.Lane, null);

        if (p.X < HeaderW)
        {
            if (row.Kind == RowKind.Video) return new Hit(HitKind.Header, row);
            if (ToggleRect(row).Contains(p)) return new Hit(HitKind.Toggle, row);
            if (SoloRect(row).Contains(p)) return new Hit(HitKind.SoloBtn, row);
            if (row.Kind == RowKind.Audio && VolumeRect(row).Contains(p)) return new Hit(HitKind.Volume, row);
            if (GripRect(row).Contains(p)) return new Hit(HitKind.Grip, row);
            return new Hit(HitKind.Header, row);
        }

        var s = Store.State;
        double Dx(double t) => Math.Abs(TimeToX(t) - p.X);
        switch (row.Kind)
        {
            case RowKind.Video:
            {
                int best = -1;
                bool left = false;
                double bestScore = double.MaxValue;
                for (int i = 0; i < s.Segments.Length; i++)
                {
                    foreach (var isStart in new[] { true, false })
                    {
                        var t = isStart ? s.Segments[i].Start : s.Segments[i].End;
                        var d = Dx(t);
                        if (d > EdgeHit) continue;
                        // a start grabs from its right, an end from its left
                        var wrongSide = isStart ? p.X < TimeToX(t) : p.X > TimeToX(t);
                        var score = d + (wrongSide ? EdgeHit : 0);
                        if (score < bestScore) { bestScore = score; best = i; left = isStart; }
                    }
                }
                if (best >= 0) return new Hit(HitKind.SegEdge, row, best, left);
                var idx = Edits.SegmentAt(s, XToTime(p.X));
                return new Hit(idx >= 0 ? HitKind.Segment : HitKind.Lane, row, idx);
            }
            case RowKind.Audio:
            {
                var lane = row.Lane!;
                for (int i = 0; i < lane.Mutes.Length; i++)
                {
                    if (Dx(lane.Mutes[i].Start) <= EdgeHit) return new Hit(HitKind.MuteEdge, row, i, true);
                    if (Dx(lane.Mutes[i].End) <= EdgeHit) return new Hit(HitKind.MuteEdge, row, i, false);
                }
                var t = XToTime(p.X);
                for (int i = 0; i < lane.Mutes.Length; i++)
                    if (t >= lane.Mutes[i].Start && t <= lane.Mutes[i].End) return new Hit(HitKind.Mute, row, i);
                return new Hit(HitKind.Lane, row);
            }
            case RowKind.Clip:
            {
                var lane = row.Lane!;
                foreach (var c in lane.Clips)
                {
                    var x0 = TimeToX(c.Start);
                    var x1 = TimeToX(c.End);
                    var edge = Math.Min(EdgeHit, (x1 - x0) / 3);
                    if (Math.Abs(p.X - x0) <= edge) return new Hit(HitKind.ClipEdge, row, Left: true, ClipId: c.Id);
                    if (Math.Abs(p.X - x1) <= edge) return new Hit(HitKind.ClipEdge, row, Left: false, ClipId: c.Id);
                    if (p.X > x0 && p.X < x1) return new Hit(HitKind.Clip, row, ClipId: c.Id);
                }
                return new Hit(HitKind.Lane, row);
            }
        }
        return new Hit(HitKind.Lane, row);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Store == null || Duration <= 0) return;
        var p = e.GetPosition(this);
        _down = p;
        _downT = Math.Clamp(XToTime(p.X), 0, Duration);
        var hit = HitTest(p);
        _downRow = hit.Row;
        var s = Store.State;

        if (e.ClickCount == 2)
        {
            switch (hit.Kind)
            {
                case HitKind.Volume:
                    Store.Commit(Edits.UpdateLane(s, hit.Row!.Lane!.Id, l => l with { Volume = 1 }), ChangeKind.Audio);
                    e.Handled = true;
                    return;
                case HitKind.Segment:
                    PlayFrom?.Invoke(s.Segments[hit.Index].Start);
                    e.Handled = true;
                    return;
                case HitKind.Clip when s.FindClip(hit.ClipId) is { } fc:
                    SeekRequested?.Invoke(fc.Clip.Start, true);
                    e.Handled = true;
                    return;
                case HitKind.Ruler:
                    Fit();
                    e.Handled = true;
                    return;
            }
        }

        switch (hit.Kind)
        {
            case HitKind.Ruler when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift):
                _drag = DragMode.Range;
                _dragLaneId = 0;
                break;
            case HitKind.Ruler:
                _drag = DragMode.Scrub;
                SeekRequested?.Invoke(Q(_downT), false);
                break;
            case HitKind.Overview:
                _drag = DragMode.Overview;
                CenterView(p.X);
                break;
            case HitKind.Toggle:
                Store.Commit(Edits.UpdateLane(s, hit.Row!.Lane!.Id, l => l with { Enabled = !l.Enabled }),
                    hit.Row.Kind == RowKind.Audio ? ChangeKind.Audio : ChangeKind.Clips);
                Hint?.Invoke(hit.Row.Lane!.Enabled ? (hit.Row.Kind == RowKind.Audio ? "track left out of the export" : "layer hidden") : "");
                e.Handled = true;
                return;
            case HitKind.SoloBtn:
                if (!Solo.Remove(hit.Row!.Lane!.Id)) Solo.Add(hit.Row.Lane.Id);
                SoloChanged?.Invoke();
                Hint?.Invoke(Solo.Count > 0 ? "solo: only soloed lanes play in the preview" : "");
                RequestStatic();
                e.Handled = true;
                return;
            case HitKind.Volume:
                _drag = DragMode.Volume;
                _dragLaneId = hit.Row!.Lane!.Id;
                Store.BeginGesture();
                SetVolumeFrom(p, hit.Row);
                break;
            case HitKind.Grip:
            case HitKind.Header:
                if (hit.Row?.Lane != null)
                {
                    Select(new Selection(SelKind.Lane, LaneId: hit.Row.Lane.Id));
                    _drag = DragMode.LaneReorder;
                    _dragLaneId = hit.Row.Lane.Id;
                    _reorderY = null;
                    _reorderTo = -1;
                }
                else if (hit.Row?.Kind == RowKind.Video)
                {
                    Select(Selection.None);
                }
                break;
            case HitKind.SegEdge:
                _drag = DragMode.SegEdge;
                _dragIndex = hit.Index;
                _dragLeft = hit.Left;
                Store.BeginGesture();
                Select(new Selection(SelKind.Segment, hit.Index));
                break;
            case HitKind.MuteEdge:
                _drag = DragMode.MuteEdge;
                _dragIndex = hit.Index;
                _dragLeft = hit.Left;
                _dragLaneId = hit.Row!.Lane!.Id;
                Store.BeginGesture();
                Select(new Selection(SelKind.Mute, hit.Index, hit.Row.Lane.Id));
                break;
            case HitKind.ClipEdge:
                _drag = DragMode.ClipTrim;
                _dragClipId = hit.ClipId;
                _dragLeft = hit.Left;
                Store.BeginGesture();
                Select(new Selection(SelKind.Clip, ClipId: hit.ClipId, LaneId: hit.Row!.Lane!.Id));
                break;
            case HitKind.Clip:
            {
                var c = s.FindClip(hit.ClipId)!.Value.Clip;
                _drag = DragMode.Pending;
                _dragClipId = c.Id;
                _grabOffset = _downT - c.Start;
                _pendingSel = new Selection(SelKind.Clip, ClipId: c.Id, LaneId: hit.Row!.Lane!.Id);
                Select(_pendingSel);
                break;
            }
            case HitKind.Mute:
                _drag = DragMode.Pending;
                _pendingSel = new Selection(SelKind.Mute, hit.Index, hit.Row!.Lane!.Id);
                break;
            case HitKind.Segment:
                _drag = DragMode.Pending;
                _pendingSel = new Selection(SelKind.Segment, hit.Index);
                break;
            case HitKind.Lane:
                _drag = DragMode.Pending;
                _pendingSel = hit.Row?.Lane != null ? new Selection(SelKind.Lane, LaneId: hit.Row.Lane.Id) : Selection.None;
                break;
            default:
                return;
        }
        CaptureMouse();
        e.Handled = true;
    }

    private void SetVolumeFrom(Point p, Row row)
    {
        var vr = VolumeRect(row);
        var v = Math.Clamp((p.X - vr.X) / vr.Width * 2, 0, 2);
        if (Math.Abs(v - 1) < 0.04) v = 1; // detent at 100%
        Store!.Preview(Edits.UpdateLane(Store.State, _dragLaneId, l => l with { Volume = Math.Round(v, 2) }), ChangeKind.Audio);
        _bubble = ($"{Math.Round(v * 100)}%", HeaderW + 40, row.Y + 2);
        RequestFx();
    }

    private void CenterView(double x)
    {
        if (_viewSpan <= 0) return;
        var t = (x - HeaderW) / LaneW * Duration;
        _viewStart = Math.Clamp(t - _viewSpan / 2, 0, Math.Max(0, Duration - _viewSpan));
        RequestStatic();
        DrawHead();
    }

    private void SeekThrottled(double t, bool exact)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastSeek).TotalMilliseconds < 30) return;
        _lastSeek = now;
        SeekRequested?.Invoke(t, exact);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Store == null || Duration <= 0) return;
        var p = e.GetPosition(this);
        var t = Math.Clamp(XToTime(p.X), 0, Duration);
        var s = Store.State;

        if (_drag == DragMode.Pending && (Math.Abs(p.X - _down.X) > 4 || Math.Abs(p.Y - _down.Y) > 4))
        {
            if (_pendingSel.Kind == SelKind.Clip)
            {
                _drag = DragMode.ClipMove;
                Store.BeginGesture();
            }
            else
            {
                _drag = DragMode.Range;
                _dragLaneId = _downRow?.Lane?.Id ?? 0;
            }
        }

        switch (_drag)
        {
            case DragMode.Scrub:
            {
                var q = Snap(t);
                SeekThrottled(q, false);
                _bubble = (TimeFmt.Precise(q), p.X, RulerH + 2);
                RequestFx();
                break;
            }
            case DragMode.Overview:
                CenterView(p.X);
                break;
            case DragMode.Volume:
                if (_downRow != null) SetVolumeFrom(p, _downRow);
                break;
            case DragMode.SegEdge:
            {
                var self = s.Segments[_dragIndex];
                var q = Snap(t, c => Math.Abs(c - (_dragLeft ? self.Start : self.End)) < 1e-9);
                Store.Preview(Edits.MoveSegmentEdge(s, _dragIndex, _dragLeft, q, Duration), ChangeKind.Segments);
                var seg = Store.State.Segments[_dragIndex];
                var edge = _dragLeft ? seg.Start : seg.End;
                // show the exact frame the cut lands on: first kept frame, or last kept frame
                SeekThrottled(_dragLeft ? edge : Math.Max(0, edge - (Media?.FrameDuration ?? 0.033)), true);
                var frame = Fps > 0 ? $" · frame {Math.Round(edge * Fps)}" : "";
                _bubble = ($"{(_dragLeft ? "starts" : "ends")} {TimeFmt.Precise(edge)}{frame}", TimeToX(edge), (_downRow?.Y ?? RulerH) - 22);
                RequestFx();
                break;
            }
            case DragMode.MuteEdge:
            {
                var lane = s.FindLane(_dragLaneId);
                if (lane == null || _dragIndex >= lane.Mutes.Length) break;
                var self = lane.Mutes[_dragIndex];
                var q = Snap(t, c => Math.Abs(c - (_dragLeft ? self.Start : self.End)) < 1e-9);
                Store.Preview(Edits.MoveMuteEdge(s, _dragLaneId, _dragIndex, _dragLeft, q, Duration), ChangeKind.Audio);
                _bubble = ($"mute {(_dragLeft ? "from" : "to")} {TimeFmt.Precise(q)}", TimeToX(q), (_downRow?.Y ?? RulerH) - 22);
                SeekThrottled(q, true);
                RequestFx();
                break;
            }
            case DragMode.ClipTrim:
            {
                var q = Snap(t);
                Store.Preview(Edits.TrimClip(s, _dragClipId, _dragLeft, q, Duration), ChangeKind.Clips);
                if (Store.State.FindClip(_dragClipId) is { } fc)
                {
                    var edge = _dragLeft ? fc.Clip.Start : fc.Clip.End;
                    _bubble = ($"{TimeFmt.Precise(edge)} · {TimeFmt.Dur(fc.Clip.Duration)}", TimeToX(edge), (_downRow?.Y ?? RulerH) - 22);
                }
                RequestFx();
                break;
            }
            case DragMode.ClipMove:
            {
                if (s.FindClip(_dragClipId) is not { } fc) break;
                var start = t - _grabOffset;
                // snap either end of the clip
                var snappedStart = Snap(start, c => Math.Abs(c - fc.Clip.Start) < 1e-9 || Math.Abs(c - fc.Clip.End) < 1e-9);
                var startSnapX = _snapX;
                var snappedEnd = Snap(start + fc.Clip.Duration, c => Math.Abs(c - fc.Clip.Start) < 1e-9 || Math.Abs(c - fc.Clip.End) < 1e-9);
                if (startSnapX != null) { start = snappedStart; _snapX = startSnapX; }
                else if (_snapX != null) start = snappedEnd - fc.Clip.Duration;
                else start = Q(start);

                var rows = Rows();
                var clipRows = rows.Where(r => r.Kind == RowKind.Clip).ToList();
                int laneIndex = s.ClipLanes.IndexOf(fc.Lane);
                if (clipRows.Count > 0 && p.Y < clipRows[0].Y - 2) laneIndex = -1;
                else if (RowAt(p.Y) is { Kind: RowKind.Clip } over) laneIndex = over.GroupIndex;
                Store.Preview(Edits.MoveClip(s, _dragClipId, start, laneIndex, Duration), ChangeKind.Clips);
                if (Store.State.FindClip(_dragClipId) is { } moved)
                {
                    Sel = Sel with { LaneId = moved.Lane.Id };
                    _bubble = ($"{TimeFmt.Precise(moved.Clip.Start)}", TimeToX(moved.Clip.Start) + 30, RulerH + 2);
                }
                RequestFx();
                break;
            }
            case DragMode.Range:
            {
                var q = Snap(t);
                var a0 = Snap(_downT);
                _snapX = TimeToX(q);
                var a = Math.Min(a0, q);
                var b = Math.Max(a0, q);
                Sel = new Selection(SelKind.Range, LaneId: _dragLaneId, A: a, B: b);
                SelectionChanged?.Invoke();
                RequestStatic();
                break;
            }
            case DragMode.LaneReorder:
            {
                if (Math.Abs(p.Y - _down.Y) < 4) break;
                var lane = s.FindLane(_dragLaneId);
                if (lane == null) break;
                var group = Rows().Where(r => r.Lane != null && r.Lane.Kind == lane.Kind).ToList();
                if (group.Count < 2) break;
                int to = group.Count - 1;
                for (int i = 0; i < group.Count; i++)
                {
                    if (p.Y < group[i].Y + group[i].H / 2) { to = i; break; }
                }
                _reorderTo = to;
                var from = group.FindIndex(r => r.Lane!.Id == _dragLaneId);
                var lineRow = to <= from ? group[to] : group[to];
                _reorderY = to <= from ? lineRow.Y - 1 : lineRow.Y + lineRow.H + 1;
                Cursor = Cursors.SizeNS;
                RequestFx();
                break;
            }
            case DragMode.Pan:
            {
                var dt = (p.X - _down.X) / LaneW * Span;
                _viewStart = Math.Clamp(_panStartView - dt, 0, Math.Max(0, Duration - Span));
                RequestStatic();
                DrawHead();
                break;
            }
            case DragMode.None:
                UpdateHover(p);
                break;
        }
    }

    private void UpdateHover(Point p)
    {
        var hit = HitTest(p);
        Cursor = hit.Kind switch
        {
            HitKind.SegEdge or HitKind.MuteEdge or HitKind.ClipEdge => Cursors.SizeWE,
            HitKind.Clip => Cursors.SizeAll,
            HitKind.Toggle or HitKind.SoloBtn or HitKind.Volume => Cursors.Hand,
            HitKind.Grip => Cursors.SizeNS,
            HitKind.Ruler => Cursors.IBeam,
            _ => Cursors.Arrow,
        };
        double? edgeX = null;
        string? tip = null;
        if (hit.Row != null && Store != null)
        {
            var s = Store.State;
            if (hit.Kind == HitKind.SegEdge)
            {
                var seg = s.Segments[hit.Index];
                var t = hit.Left ? seg.Start : seg.End;
                edgeX = TimeToX(t);
                tip = $"{(hit.Left ? "starts" : "ends")} {TimeFmt.Precise(t)}";
            }
            else if (hit.Kind == HitKind.MuteEdge && hit.Row.Lane != null)
            {
                var m = hit.Row.Lane.Mutes[hit.Index];
                var t = hit.Left ? m.Start : m.End;
                edgeX = TimeToX(t);
                tip = $"mute {(hit.Left ? "from" : "to")} {TimeFmt.Precise(t)}";
            }
            else if (hit.Kind == HitKind.ClipEdge && s.FindClip(hit.ClipId) is { } fc)
            {
                var t = hit.Left ? fc.Clip.Start : fc.Clip.End;
                edgeX = TimeToX(t);
                tip = TimeFmt.Precise(t);
            }
        }
        var newBubble = tip != null && edgeX != null && hit.Row != null ? (tip, edgeX.Value, hit.Row.Y - 22 < RulerH ? hit.Row.Y + hit.Row.H + 2 : hit.Row.Y - 22) : ((string, double, double)?)null;
        if (edgeX != _hoverEdgeX || !Nullable.Equals(newBubble, _bubble))
        {
            _hoverEdgeX = edgeX;
            if (hit.Row != null) { _hoverEdgeTop = hit.Row.Y; _hoverEdgeBottom = hit.Row.Y + hit.Row.H; }
            _bubble = newBubble;
            RequestFx();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (Store == null) return;
        var p = e.GetPosition(this);
        var t = Math.Clamp(XToTime(p.X), 0, Duration);
        switch (_drag)
        {
            case DragMode.Scrub:
                SeekRequested?.Invoke(Snap(t), true);
                break;
            case DragMode.Volume:
                Store.EndGesture(ChangeKind.Audio);
                break;
            case DragMode.SegEdge:
                Store.EndGesture(ChangeKind.Segments);
                break;
            case DragMode.MuteEdge:
                Store.EndGesture(ChangeKind.Audio);
                break;
            case DragMode.ClipTrim:
            case DragMode.ClipMove:
                Store.EndGesture(ChangeKind.Clips);
                break;
            case DragMode.Pending:
                // a click: move the playhead there and select what was under it
                SeekRequested?.Invoke(Snap(t), true);
                Select(_pendingSel);
                break;
            case DragMode.Range:
                if (Sel.Kind == SelKind.Range && Sel.B - Sel.A < 0.01) Select(Selection.None);
                else
                {
                    SelectionChanged?.Invoke();
                    Hint?.Invoke(_dragLaneId != 0 && Store.State.AudioLanes.Any(l => l.Id == _dragLaneId)
                        ? "m mutes this part · x cuts it out · esc clears"
                        : "x cuts it out · m mutes it · esc clears");
                }
                break;
            case DragMode.LaneReorder:
                if (_reorderTo >= 0)
                    Store.Commit(Edits.MoveLane(Store.State, _dragLaneId, _reorderTo), ChangeKind.Audio | ChangeKind.Clips);
                break;
        }
        _drag = DragMode.None;
        _snapX = null;
        _bubble = null;
        _reorderY = null;
        _reorderTo = -1;
        ReleaseMouseCapture();
        RequestStatic();
        UpdateHover(p);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (Store == null || Duration <= 0) return;
        var p = e.GetPosition(this);
        var hit = HitTest(p);
        var t = Math.Clamp(XToTime(p.X), 0, Duration);
        Selection target = hit.Kind switch
        {
            HitKind.Clip or HitKind.ClipEdge => new Selection(SelKind.Clip, ClipId: hit.ClipId, LaneId: hit.Row!.Lane!.Id),
            HitKind.Mute or HitKind.MuteEdge => new Selection(SelKind.Mute, hit.Index, hit.Row!.Lane!.Id),
            HitKind.Segment or HitKind.SegEdge => new Selection(SelKind.Segment, hit.Index),
            HitKind.Header or HitKind.Grip or HitKind.Toggle or HitKind.SoloBtn or HitKind.Volume when hit.Row?.Lane != null
                => new Selection(SelKind.Lane, LaneId: hit.Row.Lane.Id),
            _ => Selection.None,
        };
        // right-click inside the current range keeps it as the target
        if (Sel.Kind == SelKind.Range && t >= Sel.A && t <= Sel.B) target = Sel;
        else if (target.Kind != SelKind.None) Select(target);
        ContextRequested?.Invoke(target, t, p);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle || Duration <= 0) return;
        _drag = DragMode.Pan;
        _down = e.GetPosition(this);
        _panStartView = ViewStart;
        if (_viewSpan <= 0) { _drag = DragMode.None; return; }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Middle || _drag != DragMode.Pan) return;
        _drag = DragMode.None;
        ReleaseMouseCapture();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_drag != DragMode.None) return;
        _hoverEdgeX = null;
        _bubble = null;
        RequestFx();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Store == null || Duration <= 0) return;
        var p = e.GetPosition(this);
        var mods = Keyboard.Modifiers;
        var overflow = ContentHeight() - (LanesBottom - RulerH - 2);
        if (mods.HasFlag(ModifierKeys.Control))
        {
            Zoom(e.Delta > 0 ? 1 / 1.35 : 1.35, p.X >= HeaderW ? XToTime(p.X) : _playhead);
        }
        else if ((mods.HasFlag(ModifierKeys.Shift) || p.X < HeaderW || _viewSpan <= 0) && overflow > 0)
        {
            _scrollY = Math.Clamp(_scrollY - e.Delta / 3.0, 0, overflow);
            RequestStatic();
        }
        else if (_viewSpan > 0)
        {
            var shift = Span * 0.12 * (e.Delta > 0 ? -1 : 1);
            _viewStart = Math.Clamp(_viewStart + shift, 0, Math.Max(0, Duration - Span));
            RequestStatic();
            DrawHead();
        }
        else
        {
            // nothing to scroll: wheel zooms, the fastest way in
            Zoom(e.Delta > 0 ? 1 / 1.35 : 1.35, p.X >= HeaderW ? XToTime(p.X) : _playhead);
        }
        Playhead = _playhead;
        e.Handled = true;
    }
}
