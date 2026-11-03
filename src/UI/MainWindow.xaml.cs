using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cut.Core;

namespace Cut.UI;

public partial class MainWindow : Window, IEditor
{
    private readonly Config _config;
    private readonly Inspector _inspector;

    private MpvPlayer? _player;
    private MpvHost? _host;
    private PreviewCompositor? _comp;
    private MediaInfo? _media;
    private Keyframes? _keys;
    private Thumbnails? _thumbs;
    private CancellationTokenSource? _mediaCts;
    private readonly Dictionary<int, Waveform> _waves = new();
    private readonly Dictionary<string, Waveform> _clipWaves = new();
    private string? _path;
    private double _duration;
    private double _playhead;
    private bool _paused = true;
    private double _skipTarget = -1;
    private readonly Stopwatch _sincePos = Stopwatch.StartNew();
    private bool _renderHooked;
    private DateTime _lastTimeText;
    private string _appliedGraph = "\0";
    private bool _fullscreen;
    private (WindowState State, WindowStyle Style, GridLength Bottom) _beforeFull;

    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _graphTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _redrawTimer;

    public ProjectStore Store { get; } = new();
    public MediaInfo? Media => _media;
    public double Duration => _duration;
    public double Playhead => _playhead;
    public bool FastMode => FastModeRadio.IsChecked == true;
    public Keyframes? Keys => _keys;
    public Func<Task>? DemoHook { get; set; }
    public (int X, int Y, int W, int H)? VideoRect => _player?.VideoRect();

    public MainWindow()
    {
        InitializeComponent();
        _config = Config.Load();
        Width = Math.Max(_config.WindowWidth, MinWidth);
        Height = Math.Max(_config.WindowHeight, MinHeight);
        if (_config.Maximized) WindowState = WindowState.Maximized;
        BottomRow.Height = new GridLength(Math.Clamp(_config.TimelineHeight + 42, 170, 900));
        PreviewVolumeSlider.Value = Math.Clamp(_config.PreviewVolume, 0, 100);
        SkipCutsToggle.IsChecked = _config.SkipCuts;
        InspectorToggle.IsChecked = _config.InspectorOpen;
        if (_config.ExportMode == "fast") FastModeRadio.IsChecked = true;

        _inspector = new Inspector(InspectorHost, this) { SelectionSource = () => Timeline.Sel };
        Timeline.Store = Store;
        Timeline.WaveFor = l => l.SourceRel >= 0 && _waves.TryGetValue(l.SourceRel, out var w) ? w : null;
        Timeline.ClipWaveFor = ClipWave;
        Timeline.SeekRequested += (t, exact) => SeekTo(t, exact);
        Timeline.SelectionChanged += OnSelectionChanged;
        Timeline.PlayFrom += PlayFrom;
        Timeline.SoloChanged += () => { ScheduleGraph(); _comp?.SetState(Store.State, Timeline.Solo); };
        Timeline.ContextRequested += ShowContextMenu;
        Timeline.Hint += SetStatus;
        Store.Changed += OnStoreChanged;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveProject(); };
        _graphTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _graphTimer.Tick += (_, _) => { _graphTimer.Stop(); ApplyAudioGraph(); };
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusText.Text = ""; };
        _redrawTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _redrawTimer.Tick += (_, _) => { _redrawTimer.Stop(); Timeline.InvalidateContent(); };

        SourceInitialized += (_, _) => Native.DarkTitleBar(this);
        Loaded += OnLoaded;
        ApplyInspectorVisibility();
        ShowRecent();
        _inspector.Show(Selection.None, Store.State);
        Timeline.FastMode = FastMode;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Encoders.StartDetection();
        try
        {
            await EnsurePlayerAsync();
        }
        catch (DllNotFoundException ex)
        {
            Logger.Error("libmpv load failed", ex);
            MessageBox.Show(this, "libmpv-2.dll could not be loaded. reinstall cut to fix this.", "cut",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            Logger.Error("player init", ex);
        }
        _ = CheckForUpdateAsync();
        if (DemoHook != null) await DemoHook();
    }

    // ---- player ----

    private Task? _playerInit;

    private Task EnsurePlayerAsync() => _playerInit ??= InitPlayerAsync();

    private async Task InitPlayerAsync()
    {
        // hidden until a file opens: nothing wpf draws (the empty state) can show through an hwnd
        _host = new MpvHost { Focusable = false, Visibility = Visibility.Collapsed };
        VideoArea.Children.Insert(0, _host);
        var wid = await _host.HwndReady;

        _player = new MpvPlayer(MpvRole.Embedded, wid, a => Dispatcher.BeginInvoke(a, DispatcherPriority.Render));
        _player.SetVolume(PreviewVolumeSlider.Value);
        _comp = new PreviewCompositor(_player, a => Dispatcher.BeginInvoke(a));
        _comp.SetMasterVolume(PreviewVolumeSlider.Value);

        _player.TimePosChanged += OnTimePos;
        _player.PauseChanged += p =>
        {
            _paused = p;
            PlayBtn.Content = p ? "" : "";
            SetRendering(!p);
            _comp?.Tick(_playhead, !p, false);
        };
        _player.FileLoaded += () =>
        {
            ReleaseVideoInput();
            _appliedGraph = "\0";
            ApplyAudioGraph();
            _comp?.SetVideoRect(_player.VideoRect());
            // mpv creates its window lazily; catch it once it exists
            Dispatcher.BeginInvoke(ReleaseVideoInput, DispatcherPriority.Background);
        };
        _player.OsdDimensionsChanged += () =>
        {
            ReleaseVideoInput();
            _comp?.SetVideoRect(_player.VideoRect());
            UpdateOutline();
        };
        _player.EndOfFile += () => PlayBtn.Content = "";
    }

    private void ReleaseVideoInput() => _host?.ReleaseInput();

    private void OnTimePos(double t)
    {
        _playhead = t;
        _sincePos.Restart();
        if (!_renderHooked) Timeline.Playhead = t;
        UpdateTimeText(force: _paused);
        _comp?.Tick(t, !_paused, false);

        // preview only what is kept: hop over cuts while playing
        if (!_paused && SkipCutsToggle.IsChecked == true && _duration > 0)
        {
            if (_skipTarget >= 0 && t < _skipTarget - 0.08) return;
            _skipTarget = -1;
            var map = new TimeMap(Store.State.Segments);
            var next = map.NextKept(t + 0.01);
            if (next == null)
            {
                _player?.SetPause(true);
                var last = Store.State.Segments.LastOrDefault();
                if (last.End > 0) SeekTo(Math.Max(0, last.End - 0.001), true);
            }
            else if (next.Value > t + 0.03)
            {
                _skipTarget = next.Value;
                _player?.Seek(next.Value, exact: true);
            }
        }
    }

    // smooth playhead while playing: interpolate between mpv's per-frame updates at display rate
    private void SetRendering(bool on)
    {
        if (on == _renderHooked) return;
        _renderHooked = on;
        if (on) CompositionTarget.Rendering += OnRenderFrame;
        else
        {
            CompositionTarget.Rendering -= OnRenderFrame;
            Timeline.Playhead = _playhead;
        }
    }

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        var t = _playhead + Math.Min(_sincePos.Elapsed.TotalSeconds, 0.12);
        if (_duration > 0) t = Math.Min(t, _duration);
        Timeline.Playhead = t;
        Timeline.Follow(t);
    }

    private void UpdateTimeText(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastTimeText).TotalMilliseconds < 60) return;
        _lastTimeText = now;
        TimeText.Text = TimeFmt.Clock(_playhead);
    }

    private void SeekTo(double t, bool exact)
    {
        if (_player == null || _duration <= 0) return;
        t = Math.Clamp(t, 0, _duration);
        _skipTarget = -1;
        _player.Seek(t, exact);
        _playhead = t;
        _sincePos.Restart();
        Timeline.Playhead = t;
        UpdateTimeText(force: true);
        _comp?.Tick(t, !_paused, true);
    }

    public void Seek(double t) => SeekTo(t, true);

    public void PlayFrom(double t)
    {
        SeekTo(t, true);
        _player?.SetPause(false);
    }

    private void TogglePause()
    {
        if (_player == null || _media == null) return;
        if (_paused && _duration > 0 && _playhead >= _duration - 0.05) SeekTo(Store.State.Segments.FirstOrDefault().Start, true);
        _player.SetPause(!_paused);
    }

    // ---- opening ----

    public async void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) { SetStatus("that file is gone"); return; }
            SetStatus("opening");
            if (_host != null) _host.Visibility = Visibility.Visible;
            await EnsurePlayerAsync();
            SaveProject();

            _mediaCts?.Cancel();
            _mediaCts = new CancellationTokenSource();
            var ct = _mediaCts.Token;
            _thumbs?.Dispose();
            _thumbs = null;
            _waves.Clear();
            _keys = null;
            _media = null;
            _duration = 0;

            _player!.SetProperty("lavfi-complex", "");
            _appliedGraph = "\0";
            _player.Load(path);
            var media = await Probe.RunAsync(path, ct);
            if (ct.IsCancellationRequested) return;
            if (!media.HasVideo || media.IsStillImage)
            {
                SetStatus("no video stream in this file");
                return;
            }

            _media = media;
            _path = path;
            _duration = media.Duration > 0 ? media.Duration : _player.Duration;
            _keys = new Keyframes(media);
            _keys.Updated += () => Dispatcher.BeginInvoke(RedrawSoon);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            _thumbs = new Thumbnails(media, (int)Math.Round((TimelineView.VideoH - 6) * dpi));
            _thumbs.Updated += () => Dispatcher.BeginInvoke(RedrawSoon);
            foreach (var a in media.AudioTracks)
            {
                var w = Waveform.Start(path, a.RelIndex, _duration, ct);
                w.Updated += () => Dispatcher.BeginInvoke(RedrawSoon);
                _waves[a.RelIndex] = w;
            }

            var restored = ProjectFiles.Load(path, media);
            var state = restored != null ? Clamp(restored, _duration) : ProjectState.Fresh(media, _duration);
            Timeline.Media = media;
            Timeline.Duration = _duration;
            Timeline.Thumbs = _thumbs;
            Timeline.Keys = _keys;
            Timeline.Reset();
            _inspector.Reset();
            Store.Reset(state, _duration);
            _playhead = 0;
            Timeline.Playhead = 0;

            EmptyState.Visibility = Visibility.Collapsed;
            if (_host != null) _host.Visibility = Visibility.Visible;
            AddBtn.IsEnabled = true;
            ExportBtn.IsEnabled = true;
            FileText.Text = Path.GetFileName(path);
            InfoText.Text = media.Summary;
            DurText.Text = " / " + TimeFmt.Clock(_duration);
            Title = "cut · " + Path.GetFileName(path);
            _config.LastDir = Path.GetDirectoryName(path);
            Remember(path);
            ApplyAudioGraph();
            SetStatus(restored != null ? "picked up where you left off · ctrl+z steps back" : "");
            UpdateModeAvailability();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error($"open failed: {path}", ex);
            SetStatus("couldn't open this file");
        }
    }

    private static ProjectState Clamp(ProjectState s, double duration)
    {
        var segs = s.Segments.Where(x => x.Start < duration - 0.01)
            .Select(x => new Segment(x.Start, Math.Min(x.End, duration))).Where(x => x.Duration > 0.01).ToArray();
        if (segs.Length == 0) segs = new[] { new Segment(0, duration) };
        return s with { Segments = System.Collections.Immutable.ImmutableArray.Create(segs) };
    }

    private void RedrawSoon()
    {
        if (!_redrawTimer.IsEnabled) _redrawTimer.Start();
    }

    private Waveform? ClipWave(Clip c)
    {
        if (!c.HasAudio) return null;
        if (_clipWaves.TryGetValue(c.Path, out var w)) return w;
        w = Waveform.Start(c.Path, 0, Math.Max(c.MediaDuration, 1), CancellationToken.None);
        w.Updated += () => Dispatcher.BeginInvoke(RedrawSoon);
        _clipWaves[c.Path] = w;
        return w;
    }

    private void Remember(string path)
    {
        _config.Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _config.Recent.Insert(0, path);
        if (_config.Recent.Count > 8) _config.Recent.RemoveRange(8, _config.Recent.Count - 8);
        _config.Save();
    }

    private void ShowRecent()
    {
        RecentList.Children.Clear();
        var items = _config.Recent.Where(File.Exists).Take(6).ToList();
        RecentLabel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in items)
        {
            var b = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                ToolTip = p,
                Content = new DockPanel
                {
                    Width = 380,
                    Children =
                    {
                        new TextBlock { Text = Path.GetFileName(p), Style = (Style)FindResource("BaseText"), TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            var path = p;
            b.Click += (_, _) => OpenFile(path);
            RecentList.Children.Add(b);
        }
    }

    // ---- state changes ----

    private void OnStoreChanged(ChangeKind kind)
    {
        Timeline.InvalidateContent();
        // a selection may point at something that no longer exists
        var sel = Timeline.Sel;
        var s = Store.State;
        var valid = sel.Kind switch
        {
            SelKind.Segment => sel.Index < s.Segments.Length,
            SelKind.Mute => s.FindLane(sel.LaneId) is { } l && sel.Index < l.Mutes.Length,
            SelKind.Clip => s.FindClip(sel.ClipId) != null,
            SelKind.Lane => s.FindLane(sel.LaneId) != null,
            _ => true,
        };
        if (!valid) Timeline.Select(Selection.None);
        _inspector.Show(Timeline.Sel, s);

        if ((kind & ChangeKind.Audio) != 0 && !Store.InGesture) ScheduleGraph();
        if ((kind & ChangeKind.Clips) != 0)
        {
            _comp?.SetState(s, Timeline.Solo);
            UpdateOutline();
            UpdateModeAvailability();
        }
        if (_media != null)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    private void OnSelectionChanged()
    {
        _inspector.Show(Timeline.Sel, Store.State);
        UpdateOutline();
    }

    private void ScheduleGraph()
    {
        _graphTimer.Stop();
        _graphTimer.Start();
    }

    private void ApplyAudioGraph()
    {
        if (_player == null || _media == null || !_player.Loaded) return;
        var graph = PreviewGraph.Audio(Store.State, Timeline.Solo);
        if (graph == _appliedGraph) return;
        _appliedGraph = graph;
        if (graph.Length == 0)
        {
            _player.SetProperty("lavfi-complex", "");
            _player.SetProperty("aid", "no");
        }
        else
        {
            _player.SetProperty("aid", "auto");
            if (!_player.SetProperty("lavfi-complex", graph))
            {
                // never leave the preview silent because of a graph mpv didn't like
                Logger.Warn($"preview graph rejected: {graph}");
                _player.SetProperty("lavfi-complex", "");
                _player.SetProperty("aid", "auto");
            }
        }
    }

    private void SaveProject()
    {
        if (_path == null || _media == null) return;
        if (Store.State.IsTrimmed(_duration) || Store.State.AllClips.Any() || Store.State.AudioLanes.Any(l => l.NeedsAudioWork || !l.Enabled))
            ProjectFiles.Save(_path, Store.State);
        else
            ProjectFiles.Forget(_path);
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        _statusTimer.Stop();
        if (text.Length > 0) _statusTimer.Start();
    }

    private void UpdateModeAvailability()
    {
        var layers = Store.State.HasVisualClips;
        FastModeRadio.IsEnabled = !layers;
        FastModeRadio.ToolTip = layers ? "layers need exact mode (they have to be drawn into the picture)" : null;
        if (layers && FastModeRadio.IsChecked == true) ExactMode.IsChecked = true;
    }

    // ---- editing actions ----

    private void SplitAtPlayhead()
    {
        if (_duration <= 0) return;
        var next = Edits.Split(Store.State, _playhead);
        if (ReferenceEquals(next, Store.State)) { SetStatus("nothing to split here"); return; }
        Store.Commit(next, ChangeKind.Segments);
        var idx = Edits.SegmentAt(next, _playhead + 0.001);
        Timeline.Select(new Selection(SelKind.Segment, idx));
        SetStatus("split · select a piece and press delete to cut it");
    }

    public void CutSelection()
    {
        var sel = Timeline.Sel;
        switch (sel.Kind)
        {
            case SelKind.Range:
            {
                var next = Edits.CutRange(Store.State, sel.A, sel.B);
                if (ReferenceEquals(next, Store.State)) { SetStatus("that would cut everything"); return; }
                Store.Commit(next, ChangeKind.Segments);
                Timeline.Select(Selection.None);
                SetStatus($"cut {TimeFmt.Dur(sel.B - sel.A)} · ctrl+z to undo");
                if (_playhead > sel.A && _playhead < sel.B) SeekTo(sel.B, true);
                break;
            }
            case SelKind.Segment:
                DeleteSelection();
                break;
            default:
                SetStatus("mark a part first: drag across a lane, or use i and o");
                break;
        }
    }

    public void MuteSelection()
    {
        var sel = Timeline.Sel;
        var s = Store.State;
        if (sel.Kind == SelKind.Mute)
        {
            Store.Commit(Edits.RemoveMute(s, sel.LaneId, sel.Index), ChangeKind.Audio);
            SetStatus("unmuted");
            return;
        }
        if (sel.Kind != SelKind.Range)
        {
            SetStatus("mark a part first: drag across an audio lane, or use i and o");
            return;
        }
        var target = s.AudioLanes.FirstOrDefault(l => l.Id == sel.LaneId);
        var lanes = target != null ? new[] { target } : s.AudioLanes.Where(l => l.Enabled).ToArray();
        if (lanes.Length == 0) { SetStatus("no audio to mute"); return; }
        // marking an already fully muted part again unmutes it
        var allMuted = lanes.All(l => l.Mutes.Any(m => m.Start <= sel.A + 1e-6 && m.End >= sel.B - 1e-6));
        foreach (var l in lanes)
            s = allMuted ? Edits.Unmute(s, l.Id, sel.A, sel.B) : Edits.Mute(s, l.Id, sel.A, sel.B);
        Store.Commit(s, ChangeKind.Audio);
        Timeline.Select(Selection.None);
        var who = lanes.Length == 1 ? lanes[0].Name : "all audio";
        SetStatus(allMuted ? $"unmuted {who}" : $"muted {who} for {TimeFmt.Dur(sel.B - sel.A)}");
    }

    public void KeepOnlySelection()
    {
        var sel = Timeline.Sel;
        if (sel.Kind != SelKind.Range) return;
        var s = Edits.CutRange(Store.State, 0, sel.A);
        s = Edits.CutRange(s, sel.B, _duration);
        Store.Commit(s, ChangeKind.Segments);
        Timeline.Select(Selection.None);
        SetStatus($"kept {TimeFmt.Dur(sel.B - sel.A)}");
    }

    public void DeleteSelection()
    {
        var sel = Timeline.Sel;
        var s = Store.State;
        switch (sel.Kind)
        {
            case SelKind.Segment:
                if (s.Segments.Length <= 1) { SetStatus("can't remove the last segment"); return; }
                Store.Commit(Edits.DeleteSegment(s, sel.Index), ChangeKind.Segments);
                Timeline.Select(Selection.None);
                SetStatus("segment cut · ctrl+z to undo");
                break;
            case SelKind.Range:
                CutSelection();
                break;
            case SelKind.Mute:
                Store.Commit(Edits.RemoveMute(s, sel.LaneId, sel.Index), ChangeKind.Audio);
                Timeline.Select(Selection.None);
                break;
            case SelKind.Clip:
                Store.Commit(Edits.RemoveClip(s, sel.ClipId), ChangeKind.Clips);
                Timeline.Select(Selection.None);
                break;
            case SelKind.Lane when s.ClipLanes.Any(l => l.Id == sel.LaneId):
                Store.Commit(s with { ClipLanes = s.ClipLanes.RemoveAll(l => l.Id == sel.LaneId) }, ChangeKind.Clips);
                Timeline.Select(Selection.None);
                break;
        }
    }

    public void Select(Selection s) => Timeline.Select(s);

    private void MarkIn()
    {
        if (_duration <= 0) return;
        var sel = Timeline.Sel;
        var b = sel.Kind == SelKind.Range && sel.B > _playhead + 0.01 ? sel.B : Math.Min(_duration, _playhead + Math.Max(1, _duration * 0.02));
        Timeline.Select(new Selection(SelKind.Range, LaneId: sel.Kind == SelKind.Range ? sel.LaneId : 0, A: _playhead, B: b));
    }

    private void MarkOut()
    {
        if (_duration <= 0) return;
        var sel = Timeline.Sel;
        var a = sel.Kind == SelKind.Range && sel.A < _playhead - 0.01 ? sel.A : Math.Max(0, _playhead - Math.Max(1, _duration * 0.02));
        Timeline.Select(new Selection(SelKind.Range, LaneId: sel.Kind == SelKind.Range ? sel.LaneId : 0, A: a, B: _playhead));
    }

    private IEnumerable<double> EditPoints()
    {
        var s = Store.State;
        yield return 0;
        yield return _duration;
        foreach (var seg in s.Segments) { yield return seg.Start; yield return seg.End; }
        foreach (var c in s.AllClips) { yield return c.Start; yield return c.End; }
        foreach (var l in s.AudioLanes) foreach (var m in l.Mutes) { yield return m.Start; yield return m.End; }
    }

    private void JumpEdit(int dir)
    {
        if (_duration <= 0) return;
        var pts = EditPoints().Distinct().OrderBy(x => x).ToList();
        double? target = dir > 0 ? pts.FirstOrDefault(p => p > _playhead + 0.005, double.NaN) : pts.LastOrDefault(p => p < _playhead - 0.005, double.NaN);
        if (target is double t && !double.IsNaN(t)) SeekTo(t, true);
    }

    // ---- media layers ----

    public void AddMedia() => AddMediaVia(null, null);

    private void AddMediaVia(double? at, int? laneId)
    {
        if (_media == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "images, videos, audio|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp;*.mp4;*.mkv;*.mov;*.webm;*.avi;*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.flac|all files|*.*",
            InitialDirectory = _config.LastMediaDir ?? _config.LastDir ?? "",
        };
        if (dlg.ShowDialog(this) != true) return;
        _config.LastMediaDir = Path.GetDirectoryName(dlg.FileNames[0]);
        _ = AddMediaFiles(dlg.FileNames, at ?? _playhead, laneId);
    }

    public async Task AddMediaFiles(IEnumerable<string> files, double at, int? laneId)
    {
        if (_media == null) return;
        foreach (var file in files)
        {
            try
            {
                var m = await Probe.RunAsync(file);
                var kind = m.GuessClipKind();
                double dur = kind switch
                {
                    ClipKind.Image => 3,
                    ClipKind.Gif => m.Duration > 0 ? Math.Max(m.Duration, 2) : 3,
                    _ => m.Duration > 0 ? m.Duration : 3,
                };
                dur = Math.Min(dur, Math.Max(0.5, _duration));
                var start = Math.Clamp(at, 0, Math.Max(0, _duration - dur));
                // images keep their natural size when smaller than the frame
                var w = kind == ClipKind.Audio ? 0.35
                    : m.Width > 0 && _media.Width > 0 ? Math.Clamp((double)m.Width / _media.Width, 0.12, 0.5) : 0.35;
                var clip = new Clip
                {
                    Kind = kind,
                    Path = file,
                    Start = start,
                    Duration = dur,
                    MediaDuration = kind == ClipKind.Image ? 0 : m.Duration,
                    MediaWidth = m.Width,
                    MediaHeight = m.Height,
                    HasAudio = m.AudioTracks.Count > 0 && kind != ClipKind.Gif,
                    W = w,
                };
                var next = Edits.AddClip(Store.State, clip, laneId, out var id);
                Store.Commit(next, ChangeKind.Clips);
                var lane = next.FindClip(id)?.Lane;
                Timeline.Select(new Selection(SelKind.Clip, ClipId: id, LaneId: lane?.Id ?? 0));
                SetStatus(kind == ClipKind.Audio
                    ? $"added {Path.GetFileName(file)} · drag it on the timeline to move it"
                    : $"added {Path.GetFileName(file)} · drag it on the video to place it");
                at = start + dur;
            }
            catch (Exception ex)
            {
                Logger.Warn($"add media {file}: {ex.Message}");
                SetStatus($"couldn't read {Path.GetFileName(file)}");
            }
        }
    }

    // ---- dragging layers on the video ----

    private enum VideoDrag { None, Click, Move, Resize }
    private VideoDrag _vdrag;
    private Point _vdown;
    private int _vclip;
    private (double X, double Y, double W) _vstart;

    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private Clip? HitLayer(Point p, out bool corner)
    {
        corner = false;
        if (_comp == null) return null;
        var px = p.X * Dpi;
        var py = p.Y * Dpi;
        var sel = Timeline.Sel.Kind == SelKind.Clip ? Store.State.FindClip(Timeline.Sel.ClipId)?.Clip : null;
        if (sel != null && sel.IsVisual && _playhead >= sel.Start && _playhead < sel.End && _comp.Place(sel) is { } sp)
        {
            var cx = sp.X - sp.SrcX + sp.FullW;
            var cy = sp.Y - sp.SrcY + sp.FullH;
            if (Math.Abs(px - cx) < 14 * Dpi && Math.Abs(py - cy) < 14 * Dpi) { corner = true; return sel; }
        }
        // top layer first
        foreach (var lane in Store.State.ClipLanes)
        {
            if (!lane.Enabled) continue;
            foreach (var c in lane.Clips.Where(c => c.IsVisual && _playhead >= c.Start && _playhead < c.End))
            {
                if (_comp.Place(c) is not { } pl) continue;
                if (px >= pl.X && px < pl.X + pl.W && py >= pl.Y && py < pl.Y + pl.H) return c;
            }
        }
        return null;
    }

    private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_media == null) return;
        var p = e.GetPosition(VideoArea);
        if (e.ClickCount == 2)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        _vdown = p;
        var hit = HitLayer(p, out var corner);
        if (hit != null)
        {
            var lane = Store.State.FindClip(hit.Id)?.Lane;
            Timeline.Select(new Selection(SelKind.Clip, ClipId: hit.Id, LaneId: lane?.Id ?? 0));
            _vclip = hit.Id;
            _vstart = (hit.X, hit.Y, hit.W);
            _vdrag = corner ? VideoDrag.Resize : VideoDrag.Move;
            Store.BeginGesture();
        }
        else
        {
            _vdrag = VideoDrag.Click;
        }
        VideoArea.CaptureMouse();
        e.Handled = true;
    }

    private void OnVideoMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(VideoArea);
        if (_vdrag is VideoDrag.None or VideoDrag.Click)
        {
            if (_media == null) return;
            var hit = HitLayer(p, out var corner);
            VideoArea.Cursor = hit == null ? null : corner ? Cursors.SizeNWSE : Cursors.SizeAll;
            return;
        }
        if (_player?.VideoRect() is not { } r) return;
        var dx = (p.X - _vdown.X) * Dpi / r.W;
        var dy = (p.Y - _vdown.Y) * Dpi / r.H;
        if (_vdrag == VideoDrag.Move)
        {
            var nx = _vstart.X + dx;
            var ny = _vstart.Y + dy;
            // gentle magnet to the center lines
            if (Math.Abs(nx - 0.5) < 0.012) nx = 0.5;
            if (Math.Abs(ny - 0.5) < 0.012) ny = 0.5;
            Store.Preview(Edits.UpdateClip(Store.State, _vclip, c => c with { X = nx, Y = ny }), ChangeKind.Clips);
        }
        else
        {
            // resize from the bottom-right corner, keeping the top-left in place
            var nw = Math.Clamp(_vstart.W + dx, 0.03, 2);
            var c0 = Store.State.FindClip(_vclip)?.Clip;
            if (c0 == null) return;
            var aspect = _media?.Aspect ?? 16.0 / 9;
            var h0 = (c0 with { W = _vstart.W }).HeightFrac(aspect);
            var h1 = (c0 with { W = nw }).HeightFrac(aspect);
            var left = _vstart.X - _vstart.W / 2;
            var top = _vstart.Y - h0 / 2;
            Store.Preview(Edits.UpdateClip(Store.State, _vclip, c => c with { W = nw, X = left + nw / 2, Y = top + h1 / 2 }), ChangeKind.Clips);
        }
    }

    private void OnVideoMouseUp(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(VideoArea);
        var mode = _vdrag;
        _vdrag = VideoDrag.None;
        VideoArea.ReleaseMouseCapture();
        if (mode is VideoDrag.Move or VideoDrag.Resize)
        {
            Store.EndGesture(ChangeKind.Clips);
            return;
        }
        if (mode == VideoDrag.Click && (p - _vdown).Length < 4)
        {
            if (Timeline.Sel.Kind == SelKind.Clip) Timeline.Select(Selection.None);
            else TogglePause();
        }
    }

    private void UpdateOutline()
    {
        if (_comp == null) return;
        var sel = Timeline.Sel;
        var clip = sel.Kind == SelKind.Clip ? Store.State.FindClip(sel.ClipId)?.Clip : null;
        _comp.ShowOutline(clip != null && clip.IsVisual ? clip : null, _playhead);
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _beforeFull = (WindowState, WindowStyle, BottomRow.Height);
            ToolbarRow.Height = new GridLength(0);
            SplitRow.Height = new GridLength(0);
            BottomRow.MinHeight = 0;
            BottomRow.Height = new GridLength(0);
            InspectorCol.Width = new GridLength(0);
            WindowStyle = WindowStyle.None;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            _fullscreen = true;
        }
        else
        {
            ToolbarRow.Height = new GridLength(46);
            SplitRow.Height = new GridLength(6);
            BottomRow.MinHeight = 170;
            BottomRow.Height = _beforeFull.Bottom;
            WindowStyle = _beforeFull.Style;
            WindowState = _beforeFull.State;
            ApplyInspectorVisibility();
            _fullscreen = false;
        }
    }

    // ---- context menu ----

    private void ShowContextMenu(Selection target, double t, Point p)
    {
        var menu = new ContextMenu();
        void Item(string header, string? key, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, InputGestureText = key ?? "", IsEnabled = enabled };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        switch (target.Kind)
        {
            case SelKind.Range:
                Item("cut out", "x", CutSelection);
                Item("mute", "m", MuteSelection);
                Item("keep only this", null, KeepOnlySelection);
                menu.Items.Add(new Separator());
                Item("clear", "esc", () => Timeline.Select(Selection.None));
                break;
            case SelKind.Segment:
                Item("split here", "s", () => { SeekTo(t, true); SplitAtPlayhead(); });
                Item("play from start", null, () => PlayFrom(Store.State.Segments[target.Index].Start));
                Item("remove segment", "del", DeleteSelection, Store.State.Segments.Length > 1);
                break;
            case SelKind.Mute:
                Item("unmute", "del", DeleteSelection);
                Item("play it", null, () => PlayFrom(Math.Max(0, Store.State.FindLane(target.LaneId)!.Mutes[target.Index].Start - 1)));
                break;
            case SelKind.Clip:
                Item("move to playhead", null, () =>
                {
                    var f = Store.State.FindClip(target.ClipId);
                    if (f == null) return;
                    Store.Commit(Edits.MoveClip(Store.State, target.ClipId, _playhead, Store.State.ClipLanes.IndexOf(f.Value.Lane), _duration), ChangeKind.Clips);
                });
                Item("remove", "del", DeleteSelection);
                break;
            case SelKind.Lane:
                var lane = Store.State.FindLane(target.LaneId);
                if (lane == null) return;
                Item(lane.Enabled ? (lane.Kind == LaneKind.Audio ? "leave out of export" : "hide") : (lane.Kind == LaneKind.Audio ? "include in export" : "show"),
                    null, () => Store.Commit(Edits.UpdateLane(Store.State, lane.Id, l => l with { Enabled = !l.Enabled }), ChangeKind.Audio | ChangeKind.Clips));
                if (lane.Kind == LaneKind.Audio)
                    Item("clear mutes", null, () => Store.Commit(Edits.UpdateLane(Store.State, lane.Id, l => l with { Mutes = l.Mutes.Clear() }), ChangeKind.Audio), lane.Mutes.Length > 0);
                else
                    Item("remove layer", "del", DeleteSelection);
                break;
            default:
                Item("split here", "s", () => { SeekTo(t, true); SplitAtPlayhead(); });
                Item("add media here", "ctrl+i", () => AddMediaVia(t, null));
                break;
        }
        menu.PlacementTarget = Timeline;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // ---- ui events ----

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenViaDialog();

    private void OpenViaDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "video|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.ts;*.m2ts;*.flv;*.wmv;*.m4v;*.mpg;*.mpeg|all files|*.*",
            InitialDirectory = _config.LastDir ?? "",
        };
        if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName);
    }

    private void OnAddClick(object sender, RoutedEventArgs e) => AddMedia();
    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePause();
    private void OnFrameBackClick(object sender, RoutedEventArgs e) => _player?.FrameBack();
    private void OnFrameForwardClick(object sender, RoutedEventArgs e) => _player?.FrameStep();
    private void OnPrevEdit(object sender, RoutedEventArgs e) => JumpEdit(-1);
    private void OnNextEdit(object sender, RoutedEventArgs e) => JumpEdit(1);
    private void OnSplitClick(object sender, RoutedEventArgs e) => SplitAtPlayhead();
    private void OnMarkIn(object sender, RoutedEventArgs e) => MarkIn();
    private void OnMarkOut(object sender, RoutedEventArgs e) => MarkOut();
    private void OnCutClick(object sender, RoutedEventArgs e) => CutSelection();
    private void OnMuteClick(object sender, RoutedEventArgs e) => MuteSelection();
    private void OnZoomIn(object sender, RoutedEventArgs e) => Timeline.Zoom(1 / 1.6);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Timeline.Zoom(1.6);
    private void OnZoomFit(object sender, RoutedEventArgs e) => Timeline.Fit();

    private void OnSkipCutsChanged(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        _config.SkipCuts = SkipCutsToggle.IsChecked == true;
    }

    private void OnInspectorToggle(object sender, RoutedEventArgs e)
    {
        if (_config == null) return;
        _config.InspectorOpen = InspectorToggle.IsChecked == true;
        ApplyInspectorVisibility();
    }

    private void ApplyInspectorVisibility()
    {
        var open = InspectorToggle.IsChecked == true;
        InspectorCol.Width = new GridLength(open ? 304 : 0);
        InspectorPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_config == null || Timeline == null || _inspector == null) return;
        _config.ExportMode = FastMode ? "fast" : "exact";
        Timeline.FastMode = FastMode;
        _inspector.Reset();
        _inspector.Show(Timeline.Sel, Store.State);
        if (FastMode) SetStatus("fast: copies the original, each cut starts on the keyframe shown in amber");
    }

    private void OnSplitDone(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _config.TimelineHeight = Math.Max(120, BottomRow.ActualHeight - 42);
    }

    private void OnPreviewVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _player?.SetVolume(e.NewValue);
        _comp?.SetMasterVolume(e.NewValue);
    }

    private void OnExportClick(object sender, RoutedEventArgs e) => OpenExport();

    public ExportDialog? OpenExport(bool modal = true)
    {
        if (_media == null || _duration <= 0) { SetStatus("open a video first"); return null; }
        _player?.SetPause(true);
        var dlg = new ExportDialog(_media, Store.State, _keys, _config, FastMode) { Owner = this };
        if (modal) dlg.ShowDialog();
        else dlg.Show();
        return dlg;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            if (e.Key == Key.Escape) Keyboard.ClearFocus();
            return;
        }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.O when ctrl: OpenViaDialog(); break;
            case Key.I when ctrl: AddMedia(); break;
            case Key.E when ctrl: OpenExport(); break;
            case Key.Z when ctrl && shift: Store.Redo(); break;
            case Key.Z when ctrl: Store.Undo(); break;
            case Key.Y when ctrl: Store.Redo(); break;
            case Key.Space: TogglePause(); break;
            case Key.Left when ctrl: SeekTo(_playhead - 5, true); break;
            case Key.Right when ctrl: SeekTo(_playhead + 5, true); break;
            case Key.Left when shift: SeekTo(_playhead - 1, true); break;
            case Key.Right when shift: SeekTo(_playhead + 1, true); break;
            case Key.Left: _player?.FrameBack(); break;
            case Key.Right: _player?.FrameStep(); break;
            case Key.Up: JumpEdit(-1); break;
            case Key.Down: JumpEdit(1); break;
            case Key.S when !ctrl: SplitAtPlayhead(); break;
            case Key.I: MarkIn(); break;
            case Key.O: MarkOut(); break;
            case Key.X when !ctrl: CutSelection(); break;
            case Key.M: MuteSelection(); break;
            case Key.P: SkipCutsToggle.IsChecked = SkipCutsToggle.IsChecked != true; break;
            case Key.F: ToggleFullscreen(); break;
            case Key.Delete:
            case Key.Back: DeleteSelection(); break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;
            case Key.Escape: Timeline.Select(Selection.None); break;
            case Key.Home: SeekTo(0, true); break;
            case Key.End when _duration > 0: SeekTo(_duration, true); break;
            case Key.OemPlus or Key.Add: Timeline.Zoom(1 / 1.6); break;
            case Key.OemMinus or Key.Subtract: Timeline.Zoom(1.6); break;
            case Key.OemBackslash or Key.Oem5: Timeline.Fit(); break;
            default: return;
        }
        e.Handled = true;
    }

    private static readonly HashSet<string> LayerExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".flac",
    };

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        if (_media != null && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            var onTimeline = e.GetPosition(Timeline).Y >= 0;
            SetStatus(onTimeline || files.All(f => LayerExts.Contains(Path.GetExtension(f)))
                ? "drop to add as a layer" : "drop to open, or drop on the timeline to add as a layer");
        }
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        if (_media == null)
        {
            OpenFile(files[0]);
            return;
        }
        var tp = e.GetPosition(Timeline);
        if (tp.Y >= 0 && tp.X >= TimelineView.HeaderW)
        {
            var (t, lane) = Timeline.DropTarget(tp);
            _ = AddMediaFiles(files, t, lane);
            return;
        }
        if (files.All(f => LayerExts.Contains(Path.GetExtension(f))))
        {
            _ = AddMediaFiles(files, _playhead, null);
            return;
        }
        OpenFile(files[0]);
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_fullscreen) ToggleFullscreen();
        _config.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _config.WindowWidth = ActualWidth;
            _config.WindowHeight = ActualHeight;
        }
        _config.PreviewVolume = PreviewVolumeSlider.Value;
        _config.Save();
        SaveProject();
        _mediaCts?.Cancel();
        _thumbs?.Dispose();
        _comp?.Dispose();
        _player?.Dispose();
    }

    // ---- updates ----

    private Updater.Release? _update;

    private async Task CheckForUpdateAsync()
    {
        await Task.Delay(2500);
        var rel = await Updater.CheckAsync();
        if (rel == null || rel.Tag == _config.SkippedVersion) return;
        _update = rel;
        UpdateBtn.Content = Updater.CanSelfUpdate ? $"update to {rel.Tag}" : $"{rel.Tag} is out";
        UpdateBtn.ToolTip = Updater.CanSelfUpdate
            ? "downloads the new installer and installs over this version, then reopens cut"
            : "this is the portable build: opens the download page";
        UpdateBtn.Visibility = Visibility.Visible;
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_update == null) return;
        if (!Updater.CanSelfUpdate)
        {
            Process.Start(new ProcessStartInfo(_update.PageUrl) { UseShellExecute = true });
            return;
        }
        UpdateBtn.IsEnabled = false;
        SaveProject();
        var ok = await Updater.DownloadAndRunAsync(_update, p => Dispatcher.BeginInvoke(() => UpdateBtn.Content = $"downloading {p:0}%"));
        if (ok)
        {
            Close();
            return;
        }
        UpdateBtn.IsEnabled = true;
        UpdateBtn.Content = "update failed · retry";
    }
}
