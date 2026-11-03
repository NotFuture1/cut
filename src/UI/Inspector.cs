using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Cut.Core;

namespace Cut.UI;

public interface IEditor
{
    ProjectStore Store { get; }
    MediaInfo? Media { get; }
    double Duration { get; }
    double Playhead { get; }
    bool FastMode { get; }
    Keyframes? Keys { get; }
    void Seek(double t);
    void PlayFrom(double t);
    void CutSelection();
    void MuteSelection();
    void KeepOnlySelection();
    void DeleteSelection();
    void Select(Selection s);
    void AddMedia();
}

// the right-hand panel: properties of whatever is selected. a panel is rebuilt only when
// the selection target changes; edits made elsewhere refresh the existing controls in
// place, so a slider being dragged here is never torn down mid-drag.
public sealed class Inspector
{
    private readonly StackPanel _host;
    private readonly IEditor _ed;
    private object? _key;
    private Action<ProjectState>? _refresh;
    private bool _suppress;

    public Inspector(StackPanel host, IEditor editor)
    {
        _host = host;
        _ed = editor;
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    private static Style St(string key) => (Style)Application.Current.FindResource(key);

    public void Show(Selection sel, ProjectState s)
    {
        object key = sel.Kind switch
        {
            SelKind.Segment => ("seg", sel.Index),
            SelKind.Range => ("range", sel.LaneId),
            SelKind.Mute => ("mute", sel.LaneId, sel.Index),
            SelKind.Clip => ("clip", sel.ClipId),
            SelKind.Lane => ("lane", sel.LaneId),
            _ => "none",
        };
        if (Equals(key, _key) && _refresh != null)
        {
            _suppress = true;
            try { _refresh(s); }
            finally { _suppress = false; }
            return;
        }
        _key = key;
        _host.Children.Clear();
        _refresh = null;
        _suppress = true;
        try
        {
            _refresh = sel.Kind switch
            {
                SelKind.Segment => BuildSegment(sel, s),
                SelKind.Range => BuildRange(sel, s),
                SelKind.Mute => BuildMute(sel, s),
                SelKind.Clip => BuildClip(sel, s),
                SelKind.Lane => BuildLane(sel, s),
                _ => BuildOverview(s),
            };
            _refresh?.Invoke(s);
        }
        finally
        {
            _suppress = false;
        }
    }

    public void Reset()
    {
        _key = null;
        _refresh = null;
    }

    // ---- building blocks ----

    private TextBlock Title(string text, string? sub = null)
    {
        var t = new TextBlock { Text = text, Style = St("BaseText"), FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _host.Children.Add(t);
        if (sub != null)
        {
            var d = new TextBlock { Text = sub, Style = St("DimText"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            _host.Children.Add(d);
            return d;
        }
        return t;
    }

    private void Section(string text)
    {
        _host.Children.Add(new TextBlock { Text = text, Style = St("LabelText"), Margin = new Thickness(0, 18, 0, 6) });
    }

    private TextBlock Line(string text, bool dim = true)
    {
        var t = new TextBlock { Text = text, Style = St(dim ? "DimText" : "BaseText"), Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
        _host.Children.Add(t);
        return t;
    }

    private void Field(string label, UIElement control)
    {
        var g = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
        var l = new TextBlock { Text = label, Style = St("DimText"), Width = 64, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(l, Dock.Left);
        g.Children.Add(l);
        g.Children.Add(control);
        _host.Children.Add(g);
    }

    private TimeBox Time(string label, Action<double> commit)
    {
        var tb = new TimeBox { Step = _ed.Media?.FrameDuration ?? 1.0 / 30, HorizontalAlignment = HorizontalAlignment.Left };
        tb.Committed += v => { if (!_suppress) commit(v); };
        Field(label, tb);
        return tb;
    }

    private (Slider Slider, TextBlock Value) SliderRow(string label, double min, double max, Func<double, string> fmt,
        Action<double> preview, ChangeKind kind, bool accent = false)
    {
        var dock = new DockPanel { LastChildFill = true };
        var val = new TextBlock { Style = St("DimText"), Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(val, Dock.Right);
        var sl = new Slider { Style = St(accent ? "AccentSlider" : "DarkSlider"), Minimum = min, Maximum = max, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        sl.PreviewMouseLeftButtonDown += (_, _) => _ed.Store.BeginGesture();
        sl.PreviewMouseLeftButtonUp += (_, _) => _ed.Store.EndGesture(kind);
        sl.LostMouseCapture += (_, _) => _ed.Store.EndGesture(kind);
        sl.ValueChanged += (_, e) =>
        {
            val.Text = fmt(e.NewValue);
            if (_suppress) return;
            preview(e.NewValue);
        };
        dock.Children.Add(val);
        dock.Children.Add(sl);
        Field(label, dock);
        return (sl, val);
    }

    private CheckBox Toggle(string label, Action<bool> set)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var cb = new CheckBox { Style = St("ToggleSwitch"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(cb, Dock.Right);
        cb.Checked += (_, _) => { if (!_suppress) set(true); };
        cb.Unchecked += (_, _) => { if (!_suppress) set(false); };
        dock.Children.Add(cb);
        dock.Children.Add(new TextBlock { Text = label, Style = St("BaseText"), VerticalAlignment = VerticalAlignment.Center });
        _host.Children.Add(dock);
        return cb;
    }

    private WrapPanel Buttons(params (string Text, Action Click, bool Accent)[] items)
    {
        var wp = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (text, click, accent) in items)
        {
            var b = new Button { Content = text, Style = St(accent ? "AccentButton" : "DarkButton"), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) => click();
            wp.Children.Add(b);
        }
        _host.Children.Add(wp);
        return wp;
    }

    private void Commit(ProjectState next, ChangeKind k) => _ed.Store.Commit(next, k);

    // ---- panels ----

    private Action<ProjectState>? BuildOverview(ProjectState s)
    {
        var m = _ed.Media;
        if (m == null)
        {
            Title("nothing open", "drop a video anywhere, or press ctrl+o");
            Shortcuts();
            return null;
        }
        Title(Path.GetFileName(m.FilePath), m.Summary);
        Section("edit");
        var kept = Line("", false);
        var counts = Line("");
        Buttons(("add image, video or audio", _ed.AddMedia, false));
        Shortcuts();
        return st =>
        {
            kept.Text = $"keeps {TimeFmt.Clock(st.KeptDuration)} of {TimeFmt.Clock(_ed.Duration)}";
            var cuts = Math.Max(0, st.Segments.Length - 1) + (st.Segments.Length > 0 && st.Segments[0].Start > 0.001 ? 1 : 0)
                       + (st.Segments.Length > 0 && st.Segments[^1].End < _ed.Duration - 0.001 ? 1 : 0);
            var mutes = st.AudioLanes.Sum(l => l.Mutes.Length);
            var layers = st.AllClips.Count();
            var parts = new List<string> { cuts == 1 ? "1 cut" : $"{cuts} cuts" };
            if (mutes > 0) parts.Add(mutes == 1 ? "1 muted part" : $"{mutes} muted parts");
            if (layers > 0) parts.Add(layers == 1 ? "1 layer clip" : $"{layers} layer clips");
            counts.Text = string.Join(" · ", parts);
        };
    }

    private void Shortcuts()
    {
        Section("keys");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        (string, string)[] keys =
        {
            ("space", "play / pause"),
            ("drag a lane", "mark a part"),
            ("x", "cut the marked part"),
            ("m", "mute the marked part"),
            ("s", "split at playhead"),
            ("i  /  o", "mark in / out"),
            ("← →", "one frame (shift: 1s)"),
            ("↑ ↓", "previous / next edit"),
            ("del", "remove selected"),
            ("ctrl z  /  y", "undo / redo"),
            ("ctrl wheel", "zoom"),
            ("ctrl e", "export"),
        };
        for (int i = 0; i < keys.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = keys[i].Item1, Style = St("BaseText"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 2) };
            var v = new TextBlock { Text = keys[i].Item2, Style = St("DimText"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(k, i);
            Grid.SetRow(v, i);
            Grid.SetColumn(v, 1);
            grid.Children.Add(k);
            grid.Children.Add(v);
        }
        _host.Children.Add(grid);
    }

    private Action<ProjectState>? BuildSegment(Selection sel, ProjectState s)
    {
        var idx = sel.Index;
        var title = Title("segment", "a kept part of the video");
        Section("timing");
        var start = Time("start", v => Commit(Edits.SetSegment(_ed.Store.State, idx, v, _ed.Store.State.Segments[idx].End, _ed.Duration), ChangeKind.Segments));
        var end = Time("end", v => Commit(Edits.SetSegment(_ed.Store.State, idx, _ed.Store.State.Segments[idx].Start, v, _ed.Duration), ChangeKind.Segments));
        var len = Line("");
        var fast = Line("");
        fast.Foreground = Res("WarnBrush");
        Buttons(("play", () => _ed.PlayFrom(_ed.Store.State.Segments[idx].Start), false),
            ("remove segment", _ed.DeleteSelection, false));
        return st =>
        {
            if (idx >= st.Segments.Length) return;
            var seg = st.Segments[idx];
            title.Text = $"segment {idx + 1} of {st.Segments.Length} · a kept part of the video";
            start.Value = seg.Start;
            end.Value = seg.End;
            len.Text = $"length {TimeFmt.Dur(seg.Duration)}";
            var land = _ed.FastMode ? _ed.Keys?.CachedLanding(seg.Start) : null;
            fast.Text = land is double l && seg.Start - l > 0.002
                ? $"fast export starts this {TimeFmt.Dur(seg.Start - l)} earlier, on the keyframe at {TimeFmt.Precise(l)}"
                : "";
            fast.Visibility = fast.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    private Action<ProjectState>? BuildRange(Selection sel, ProjectState s)
    {
        var lane = s.FindLane(sel.LaneId);
        var target = lane?.Kind == LaneKind.Audio ? lane.Name : "all audio";
        Title("marked part", "cut it out, mute it, or keep only it");
        Section("timing");
        var start = Time("from", v => _ed.Select(_selNow() with { A = Math.Min(v, _selNow().B - 0.01) }));
        var end = Time("to", v => _ed.Select(_selNow() with { B = Math.Max(v, _selNow().A + 0.01) }));
        var len = Line("");
        Buttons(("cut out  x", _ed.CutSelection, true), ($"mute {target}  m", _ed.MuteSelection, false),
            ("keep only this", _ed.KeepOnlySelection, false));
        return _ =>
        {
            var cur = _selNow();
            start.Value = cur.A;
            end.Value = cur.B;
            len.Text = $"length {TimeFmt.Dur(cur.B - cur.A)}";
        };
    }

    // the editor's live selection (range edits come back through it)
    public Func<Selection> SelectionSource { get; set; } = () => Selection.None;
    private Selection _selNow() => SelectionSource();

    private Action<ProjectState>? BuildMute(Selection sel, ProjectState s)
    {
        var laneId = sel.LaneId;
        var idx = sel.Index;
        var lane = s.FindLane(laneId);
        Title("muted part", lane != null ? $"silence on {lane.Name}" : null);
        Section("timing");
        var start = Time("from", v =>
        {
            var m = _ed.Store.State.FindLane(laneId)!.Mutes[idx];
            Commit(Edits.SetMute(_ed.Store.State, laneId, idx, Math.Min(v, m.End - 0.01), m.End), ChangeKind.Audio);
        });
        var end = Time("to", v =>
        {
            var m = _ed.Store.State.FindLane(laneId)!.Mutes[idx];
            Commit(Edits.SetMute(_ed.Store.State, laneId, idx, m.Start, Math.Max(v, m.Start + 0.01)), ChangeKind.Audio);
        });
        var len = Line("");
        Buttons(("play", () => _ed.PlayFrom(Math.Max(0, (_ed.Store.State.FindLane(laneId)?.Mutes.ElementAtOrDefault(idx).Start ?? 0) - 1)), false),
            ("unmute", _ed.DeleteSelection, false));
        return st =>
        {
            var l = st.FindLane(laneId);
            if (l == null || idx >= l.Mutes.Length) return;
            var m = l.Mutes[idx];
            start.Value = m.Start;
            end.Value = m.End;
            len.Text = $"length {TimeFmt.Dur(m.Duration)}";
        };
    }

    private Action<ProjectState>? BuildLane(Selection sel, ProjectState s)
    {
        var laneId = sel.LaneId;
        var lane = s.FindLane(laneId);
        if (lane == null) return BuildOverview(s);

        if (lane.Kind == LaneKind.Clips)
        {
            Title("layer", "images, videos and audio placed over the video");
            Section("lane");
            var vis = Toggle("show in export and preview", v => Commit(Edits.UpdateLane(_ed.Store.State, laneId, l => l with { Enabled = v }), ChangeKind.Clips));
            var count = Line("");
            Buttons(("add media here", _ed.AddMedia, false),
                ("remove layer", () => Commit(_ed.Store.State with { ClipLanes = _ed.Store.State.ClipLanes.RemoveAll(l => l.Id == laneId) }, ChangeKind.Clips), false));
            Line("drag a clip to move it in time or between layers. top layers draw over lower ones.").Margin = new Thickness(0, 10, 0, 0);
            return st =>
            {
                var l = st.FindLane(laneId);
                if (l == null) return;
                vis.IsChecked = l.Enabled;
                count.Text = l.Clips.Length == 1 ? "1 clip" : $"{l.Clips.Length} clips";
            };
        }

        var info = _ed.Media?.AudioTracks.ElementAtOrDefault(lane.SourceRel);
        Title(lane.Name, info?.Detail);
        Section("track");
        var include = Toggle("include in export", v => Commit(Edits.UpdateLane(_ed.Store.State, laneId, l => l with { Enabled = v }), ChangeKind.Audio));
        var (vol, _) = SliderRow("volume", 0, 200, v => $"{Math.Round(v)}%",
            v => _ed.Store.Preview(Edits.UpdateLane(_ed.Store.State, laneId, l => l with { Volume = Math.Round(v) / 100 }), ChangeKind.Audio),
            ChangeKind.Audio);
        Section("muted parts");
        var list = new StackPanel();
        _host.Children.Add(list);
        var hint = Line("drag across this lane to mark a part, then press m.");
        Buttons(("clear all mutes", () => Commit(Edits.UpdateLane(_ed.Store.State, laneId, l => l with { Mutes = l.Mutes.Clear() }), ChangeKind.Audio), false));
        var lastMutes = default(System.Collections.Immutable.ImmutableArray<Segment>);
        return st =>
        {
            var l = st.FindLane(laneId);
            if (l == null) return;
            include.IsChecked = l.Enabled;
            vol.Value = l.Volume * 100;
            if (lastMutes == l.Mutes && !lastMutes.IsDefault) return;
            lastMutes = l.Mutes;
            list.Children.Clear();
            hint.Visibility = l.Mutes.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < l.Mutes.Length; i++)
            {
                var m = l.Mutes[i];
                var index = i;
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var un = new Button { Content = "unmute", Style = St("GhostButton") };
                un.Click += (_, _) => Commit(Edits.RemoveMute(_ed.Store.State, laneId, index), ChangeKind.Audio);
                DockPanel.SetDock(un, Dock.Right);
                row.Children.Add(un);
                var go = new Button { Content = $"{TimeFmt.Precise(m.Start)} → {TimeFmt.Precise(m.End)}", Style = St("GhostButton"), HorizontalAlignment = HorizontalAlignment.Left, Foreground = Res("TextBrush") };
                go.Click += (_, _) => { _ed.Seek(m.Start); _ed.Select(new Selection(SelKind.Mute, index, laneId)); };
                row.Children.Add(go);
                list.Children.Add(row);
            }
        };
    }

    private Action<ProjectState>? BuildClip(Selection sel, ProjectState s)
    {
        var id = sel.ClipId;
        var found = s.FindClip(id);
        if (found == null) return BuildOverview(s);
        var c0 = found.Value.Clip;
        var kindText = c0.Kind switch { ClipKind.Image => "image", ClipKind.Gif => "gif", ClipKind.Video => "video", _ => "audio" };
        var detail = c0.MediaWidth > 0 ? $"{kindText} · {c0.MediaWidth}×{c0.MediaHeight}" : kindText;
        if (c0.MediaDuration > 0) detail += $" · {TimeFmt.Dur(c0.MediaDuration)}";
        Title(c0.Name, detail);

        ProjectState Upd(Func<Clip, Clip> f) => Edits.UpdateClip(_ed.Store.State, id, f);
        Clip Cur() => _ed.Store.State.FindClip(id)?.Clip ?? c0;

        Section("timing");
        var start = Time("start", v => Commit(Edits.MoveClip(_ed.Store.State, id, v,
            _ed.Store.State.ClipLanes.IndexOf(_ed.Store.State.FindClip(id)!.Value.Lane), _ed.Duration), ChangeKind.Clips));
        var length = Time("length", v => Commit(Edits.TrimClip(_ed.Store.State, id, false, Cur().Start + v, _ed.Duration), ChangeKind.Clips));
        TimeBox? inBox = null;
        if (c0.Kind is ClipKind.Video or ClipKind.Audio)
            inBox = Time("from", v => Commit(Upd(c => c with { In = Math.Clamp(v, 0, Math.Max(0, c.MediaDuration - c.Duration)) }), ChangeKind.Clips));

        Slider? size = null, xs = null, ys = null, op = null, vol = null;
        CheckBox? mute = null;
        if (c0.IsVisual)
        {
            Section("placement");
            var aspect = _ed.Media?.Aspect ?? 16.0 / 9;
            var grid = new UniformGrid { Columns = 3, Rows = 3, Width = 90, Height = 60, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(64, 2, 0, 6) };
            for (int r = 0; r < 3; r++)
            {
                for (int col = 0; col < 3; col++)
                {
                    var (rr, cc) = (r, col);
                    // an alignment picker: the dot sits where the layer will go
                    var dot = new System.Windows.Shapes.Ellipse
                    {
                        Width = 5, Height = 5, Fill = Res("TextDimBrush"),
                        HorizontalAlignment = cc == 0 ? HorizontalAlignment.Left : cc == 1 ? HorizontalAlignment.Center : HorizontalAlignment.Right,
                        VerticalAlignment = rr == 0 ? VerticalAlignment.Top : rr == 1 ? VerticalAlignment.Center : VerticalAlignment.Bottom,
                        Margin = new Thickness(5, 3, 5, 3),
                    };
                    var b = new Button
                    {
                        Style = St("DarkButton"), Padding = new Thickness(0), Margin = new Thickness(1.5),
                        ToolTip = $"{(rr == 0 ? "top" : rr == 1 ? "middle" : "bottom")} {(cc == 0 ? "left" : cc == 1 ? "center" : "right")}",
                        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                        Content = new Grid { Children = { dot } },
                    };
                    b.Click += (_, _) => Commit(Upd(c =>
                    {
                        const double m = 0.03;
                        var hFrac = c.HeightFrac(aspect);
                        var x = cc == 0 ? m + c.W / 2 : cc == 1 ? 0.5 : 1 - m - c.W / 2;
                        var y = rr == 0 ? m + hFrac / 2 : rr == 1 ? 0.5 : 1 - m - hFrac / 2;
                        return c with { X = x, Y = y };
                    }), ChangeKind.Clips);
                    grid.Children.Add(b);
                }
            }
            _host.Children.Add(grid);
            size = SliderRow("size", 2, 100, v => $"{Math.Round(v)}%",
                v => _ed.Store.Preview(Upd(c => c with { W = v / 100 }), ChangeKind.Clips), ChangeKind.Clips).Slider;
            xs = SliderRow("x", 0, 100, v => $"{Math.Round(v)}%",
                v => _ed.Store.Preview(Upd(c => c with { X = v / 100 }), ChangeKind.Clips), ChangeKind.Clips).Slider;
            ys = SliderRow("y", 0, 100, v => $"{Math.Round(v)}%",
                v => _ed.Store.Preview(Upd(c => c with { Y = v / 100 }), ChangeKind.Clips), ChangeKind.Clips).Slider;
            op = SliderRow("opacity", 0, 100, v => $"{Math.Round(v)}%",
                v => _ed.Store.Preview(Upd(c => c with { Opacity = v / 100 }), ChangeKind.Clips), ChangeKind.Clips).Slider;
            Buttons(("fill frame", () => Commit(Upd(c => c with { X = 0.5, Y = 0.5, W = 1 }), ChangeKind.Clips), false),
                ("center", () => Commit(Upd(c => c with { X = 0.5, Y = 0.5 }), ChangeKind.Clips), false));
            Line("drag it on the video to move it, drag a corner to resize.").Margin = new Thickness(0, 2, 0, 0);
        }
        if (c0.HasAudio)
        {
            Section("sound");
            mute = Toggle("play its sound", v => Commit(Upd(c => c with { Muted = !v }), ChangeKind.Clips));
            vol = SliderRow("volume", 0, 200, v => $"{Math.Round(v)}%",
                v => _ed.Store.Preview(Upd(c => c with { Volume = Math.Round(v) / 100 }), ChangeKind.Clips), ChangeKind.Clips).Slider;
        }
        Buttons(("move to playhead", () => Commit(Edits.MoveClip(_ed.Store.State, id, _ed.Playhead,
                _ed.Store.State.ClipLanes.IndexOf(_ed.Store.State.FindClip(id)!.Value.Lane), _ed.Duration), ChangeKind.Clips), false),
            ("remove", _ed.DeleteSelection, false));

        return st =>
        {
            if (st.FindClip(id) is not { } f) return;
            var c = f.Clip;
            start.Value = c.Start;
            length.Value = c.Duration;
            if (inBox != null) inBox.Value = c.In;
            if (size != null) size.Value = c.W * 100;
            if (xs != null) xs.Value = c.X * 100;
            if (ys != null) ys.Value = c.Y * 100;
            if (op != null) op.Value = c.Opacity * 100;
            if (vol != null) vol.Value = c.Volume * 100;
            if (mute != null) mute.IsChecked = !c.Muted;
        };
    }
}
