using System.Globalization;
using System.Runtime.InteropServices;

namespace Cut.Core;

// where a layer lands inside the mpv window, in physical pixels, already cropped to the
// video area. SrcX/SrcY are the crop offset into the layer's own bitmap.
public sealed record Placement(int X, int Y, int W, int H, int SrcX, int SrcY, int FullW, int FullH);

// draws the timeline's layers over the main preview as mpv osd overlays and plays their
// sound, all slaved to the main player's clock. images are decoded once per size by
// ffmpeg; video and gif layers run in their own small mpv (software render, no window).
public sealed class PreviewCompositor : IDisposable
{
    private readonly MpvPlayer _main;
    private readonly Action<Action> _post;
    private ProjectState _state = new();
    private IReadOnlySet<int> _solo = new HashSet<int>();
    private double _master = 100;
    private (int X, int Y, int W, int H)? _rect;
    private double _t;
    private bool _playing;
    private bool _disposed;

    private readonly Dictionary<int, LayerPreview> _media = new();
    private readonly Dictionary<int, ImageLayer> _images = new();
    private readonly Dictionary<int, int> _overlayIds = new();

    public PreviewCompositor(MpvPlayer main, Action<Action> post)
    {
        _main = main;
        _post = post;
    }

    public int ActiveMediaPlayers => _media.Count(m => m.Value.Player != null);

    public void SetState(ProjectState s, IReadOnlySet<int> solo)
    {
        _state = s;
        _solo = solo;
        AssignIds();
        var clips = s.AllClips.ToDictionary(c => c.Id);

        foreach (var id in _media.Keys.ToList())
        {
            if (!clips.TryGetValue(id, out var c) || c.Path != _media[id].Clip.Path || c.Kind != _media[id].Clip.Kind)
            {
                _media[id].Dispose();
                _media.Remove(id);
            }
        }
        foreach (var id in _images.Keys.ToList())
        {
            if (!clips.TryGetValue(id, out var c) || c.Path != _images[id].Clip.Path || c.Kind != ClipKind.Image)
            {
                _images[id].Dispose(_main);
                _images.Remove(id);
            }
        }
        foreach (var c in clips.Values)
        {
            if (_media.TryGetValue(c.Id, out var lp)) lp.Clip = c;
            if (_images.TryGetValue(c.Id, out var im)) im.Clip = c;
        }
        Refresh(seeked: false);
    }

    public void SetMasterVolume(double v)
    {
        _master = v;
        foreach (var m in _media.Values) m.ApplyVolume(Audible(m.Clip), _master);
    }

    public void SetVideoRect((int X, int Y, int W, int H)? rect)
    {
        _rect = rect;
        Refresh(seeked: false);
    }

    public void Tick(double t, bool playing, bool seeked)
    {
        _t = t;
        _playing = playing;
        Refresh(seeked);
    }

    private bool Audible(Clip c)
    {
        var lane = _state.ClipLanes.FirstOrDefault(l => l.Clips.Any(x => x.Id == c.Id));
        return lane != null && lane.Enabled && (_solo.Count == 0 || _solo.Contains(lane.Id)) && c.MakesSound;
    }

    private bool LaneVisible(Clip c) =>
        _state.ClipLanes.FirstOrDefault(l => l.Clips.Any(x => x.Id == c.Id))?.Enabled == true;

    private void AssignIds()
    {
        _overlayIds.Clear();
        int id = 1;
        foreach (var lane in _state.ClipLanes.Reverse())
            foreach (var c in lane.Clips.Where(c => c.IsVisual))
                if (id <= 63) _overlayIds[c.Id] = id++;
    }

    public Placement? Place(Clip c)
    {
        if (_rect is not { } r || c.MediaWidth <= 0 || c.MediaHeight <= 0) return null;
        var fw = (int)Math.Round(c.W * r.W);
        var fh = (int)Math.Round(fw * (double)c.MediaHeight / c.MediaWidth);
        if (fw < 2 || fh < 2) return null;
        var x = (int)Math.Round(r.X + c.X * r.W - fw / 2.0);
        var y = (int)Math.Round(r.Y + c.Y * r.H - fh / 2.0);
        var x0 = Math.Max(x, r.X);
        var y0 = Math.Max(y, r.Y);
        var x1 = Math.Min(x + fw, r.X + r.W);
        var y1 = Math.Min(y + fh, r.Y + r.H);
        if (x1 - x0 < 1 || y1 - y0 < 1) return null;
        return new Placement(x0, y0, x1 - x0, y1 - y0, x0 - x, y0 - y, fw, fh);
    }

    private void Refresh(bool seeked)
    {
        if (_disposed) return;
        var t = _t;
        foreach (var c in _state.AllClips)
        {
            var inRange = t >= c.Start && t < c.End;
            var near = t >= c.Start - 3 && t < c.End + 1;
            var far = t < c.Start - 12 || t > c.End + 12;

            if (c.Kind == ClipKind.Image)
            {
                if (!_images.TryGetValue(c.Id, out var im))
                {
                    if (!near) continue;
                    im = new ImageLayer(c);
                    _images[c.Id] = im;
                }
                var id = _overlayIds.GetValueOrDefault(c.Id);
                var place = inRange && LaneVisible(c) && id > 0 ? Place(c) : null;
                im.Show(_main, id, place, c.Opacity, _post, () => Refresh(false));
                if (far && place == null) { im.Dispose(_main); _images.Remove(c.Id); }
                continue;
            }

            if (!_media.TryGetValue(c.Id, out var lp))
            {
                if (!near) continue;
                lp = new LayerPreview(c, _main, _post);
                _media[c.Id] = lp;
                lp.Start(_master, Audible(c));
            }
            if (far && !inRange)
            {
                lp.Dispose();
                _media.Remove(c.Id);
                continue;
            }
            var oid = _overlayIds.GetValueOrDefault(c.Id);
            lp.OverlayId = oid;
            lp.Target = c.IsVisual && inRange && LaneVisible(c) && oid > 0 ? Place(c) : null;
            lp.ApplyVolume(Audible(c), _master);
            lp.Sync(t, _playing, seeked, inRange);
        }
        if (_outlineClip != 0) DrawOutline(_state.FindClip(_outlineClip)?.Clip, t);
    }

    // ---- selection outline: four thin accent overlays around the selected layer ----

    private const int OutlineBase = 60; // ids 60..63, above every layer
    private int _outlineClip;
    private Placement? _outlinePlace;
    private readonly List<IntPtr> _outlineMem = new();

    public void ShowOutline(Clip? clip, double t)
    {
        _outlineClip = clip?.Id ?? 0;
        DrawOutline(clip, t);
    }

    private void DrawOutline(Clip? clip, double t)
    {
        var place = clip != null && t >= clip.Start && t < clip.End && LaneVisible(clip) ? Place(clip) : null;
        if (place == _outlinePlace) return;
        _outlinePlace = place;
        if (place == null)
        {
            for (int i = 0; i < 4; i++) _main.OverlayRemove(OutlineBase + i);
            return;
        }
        const int th = 2;
        var w = Math.Max(th, place.W);
        var h = Math.Max(th, place.H);
        var horiz = Solid(w, th);
        var vert = Solid(th, h);
        _main.OverlayAdd(OutlineBase, place.X, place.Y, horiz, w, th, w * 4);
        _main.OverlayAdd(OutlineBase + 1, place.X, place.Y + h - th, horiz, w, th, w * 4);
        _main.OverlayAdd(OutlineBase + 2, place.X, place.Y, vert, th, h, th * 4);
        _main.OverlayAdd(OutlineBase + 3, place.X + w - th, place.Y, vert, th, h, th * 4);
        // mpv copies overlay pixels, so the buffers can go right away
        foreach (var m in _outlineMem) Marshal.FreeHGlobal(m);
        _outlineMem.Clear();
        _outlineMem.Add(horiz);
        _outlineMem.Add(vert);
    }

    private static IntPtr Solid(int w, int h)
    {
        var mem = Marshal.AllocHGlobal(w * h * 4);
        var px = new int[w * h];
        Array.Fill(px, unchecked((int)0xFFE5484D)); // bgra bytes 4D 48 E5 FF
        Marshal.Copy(px, 0, mem, px.Length);
        return mem;
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var m in _outlineMem) Marshal.FreeHGlobal(m);
        _outlineMem.Clear();
        foreach (var m in _media.Values) m.Dispose();
        _media.Clear();
        foreach (var im in _images.Values) im.Dispose(_main);
        _images.Clear();
    }

    // ---- image layer: one decoded bitmap per (size, opacity) ----

    private sealed class ImageLayer
    {
        public Clip Clip;
        private IntPtr _data;
        private int _w, _h, _stride;
        private float _op;
        private bool _loading;
        private int _shownId;
        private Placement? _shownPlace;
        private (int W, int H, float Op) _want;

        public ImageLayer(Clip c) => Clip = c;

        public void Show(MpvPlayer main, int id, Placement? place, double opacity, Action<Action> post, Action again)
        {
            if (place == null)
            {
                Hide(main);
                return;
            }
            var want = (place.FullW, place.FullH, (float)opacity);
            if (_data == IntPtr.Zero || (_w, _h, _op) != want)
            {
                // one decode in flight; the newest wanted size is picked up when it lands
                if (!_loading) Decode(want, post, again);
                else _want = want;
                if (_data == IntPtr.Zero || _w != want.Item1 || _h != want.Item2) return;
            }
            if (_shownId == id && _shownPlace == place) return;
            if (_shownId != 0 && _shownId != id) main.OverlayRemove(_shownId);
            var ptr = _data + place.SrcY * _stride + place.SrcX * 4;
            main.OverlayAdd(id, place.X, place.Y, ptr, place.W, place.H, _stride);
            _shownId = id;
            _shownPlace = place;
        }

        private void Hide(MpvPlayer main)
        {
            if (_shownId == 0) return;
            main.OverlayRemove(_shownId);
            _shownId = 0;
            _shownPlace = null;
        }

        private void Decode((int W, int H, float Op) want, Action<Action> post, Action again)
        {
            _loading = true;
            _want = want;
            var path = Clip.Path;
            _ = Task.Run(async () =>
            {
                var px = await DecodeImage(path, want.W, want.H, want.Op);
                post(() =>
                {
                    _loading = false;
                    if (_want != want)
                    {
                        // a newer size was requested meanwhile
                        if (px != IntPtr.Zero) Marshal.FreeHGlobal(px);
                        Decode(_want, post, again);
                        return;
                    }
                    if (px == IntPtr.Zero) return;
                    var old = _data;
                    _data = px;
                    (_w, _h, _op) = want;
                    _stride = want.W * 4;
                    _shownPlace = null; // force re-add with the new bitmap
                    again();
                    if (old != IntPtr.Zero) Marshal.FreeHGlobal(old);
                });
            });
        }

        public void Dispose(MpvPlayer main)
        {
            Hide(main);
            if (_data != IntPtr.Zero) Marshal.FreeHGlobal(_data);
            _data = IntPtr.Zero;
        }
    }

    // ffmpeg decodes any image format to bgra at the exact on-screen size
    internal static async Task<IntPtr> DecodeImage(string path, int w, int h, float opacity)
    {
        try
        {
            var px = await Proc.RunBytesAsync(ToolPaths.Ffmpeg, new[]
            {
                "-v", "error", "-nostdin", "-i", path, "-frames:v", "1",
                "-vf", $"scale={w}:{h}:flags=bicubic,format=bgra", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
            }, CancellationToken.None);
            if (px.Length != w * h * 4) return IntPtr.Zero;
            Premultiply(px, opacity);
            var mem = Marshal.AllocHGlobal(px.Length);
            Marshal.Copy(px, 0, mem, px.Length);
            return mem;
        }
        catch (Exception ex)
        {
            Logger.Warn($"image decode {path}: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    internal static void Premultiply(byte[] bgra, float opacity)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3] * opacity;
            bgra[i] = (byte)(bgra[i] * a / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] * a / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] * a / 255);
            bgra[i + 3] = (byte)a;
        }
    }
}

// a video, gif or audio layer's own player, kept in step with the main clock
internal sealed class LayerPreview : IDisposable
{
    public Clip Clip;
    public int OverlayId;
    private readonly MpvPlayer _main;
    private readonly Action<Action> _post;
    public MpvPlayer? Player;
    private MpvSwRenderer? _renderer;
    private volatile Placement? _target;
    private double _appliedOpacity = 1;
    private int _shownId;
    private bool _disposed;
    private bool _prerolled;
    private double _lastSeek = -1;
    private DateTime _lastSync;
    private double _volume = -1;
    private readonly object _overlayLock = new();

    public LayerPreview(Clip c, MpvPlayer main, Action<Action> post)
    {
        Clip = c;
        _main = main;
        _post = post;
    }

    public Placement? Target
    {
        get => _target;
        set
        {
            var changed = value != _target || Math.Abs(_appliedOpacity - Clip.Opacity) > 0.001;
            _appliedOpacity = Clip.Opacity;
            _target = value;
            if (value == null) HideOverlay();
            else if (changed && _renderer != null)
            {
                _renderer.SetTarget(value.FullW, value.FullH, Clip.Opacity);
                _renderer.Invalidate(); // re-place even when paused and no new frame comes
            }
        }
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public void Start(double master, bool audible)
    {
        var clip = Clip;
        _ = Task.Run(() =>
        {
            try
            {
                var role = clip.Kind == ClipKind.Audio ? MpvRole.Audio : MpvRole.Render;
                var p = new MpvPlayer(role, post: _post);
                MpvSwRenderer? r = role == MpvRole.Render ? new MpvSwRenderer(p) : null;
                if (clip.Kind == ClipKind.Gif) p.SetProperty("loop-file", "inf");
                if (!clip.HasAudio || clip.Kind == ClipKind.Gif) p.SetProperty("aid", "no");
                p.SetProperty("start", F(clip.In));
                p.SetVolume(audible ? clip.Volume * master : 0);
                p.Load(clip.Path);
                _post(() =>
                {
                    if (_disposed)
                    {
                        r?.Dispose();
                        p.Dispose();
                        return;
                    }
                    Player = p;
                    _renderer = r;
                    if (r != null)
                    {
                        r.Frame += OnFrame;
                        if (_target is { } t) r.SetTarget(t.FullW, t.FullH, Clip.Opacity);
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"layer preview {clip.Path}: {ex.Message}");
            }
        });
    }

    public void ApplyVolume(bool audible, double master)
    {
        var v = audible ? Clip.Volume * master : 0;
        if (Player == null || Math.Abs(v - _volume) < 0.01) return;
        _volume = v;
        Player.SetVolume(v);
    }

    private double Local(double t)
    {
        var local = t - Clip.Start + Clip.In;
        if (Clip.Kind == ClipKind.Gif && Clip.MediaDuration > 0) local %= Clip.MediaDuration;
        return Math.Max(0, local);
    }

    public void Sync(double t, bool playing, bool seeked, bool inRange)
    {
        var p = Player;
        if (p == null || !p.Loaded) return;
        var now = DateTime.UtcNow;

        if (!inRange)
        {
            if (!p.Paused) p.SetPause(true);
            // park on the first frame just before the layer starts, so it begins instantly
            if (t < Clip.Start && Clip.Start - t < 3 && !_prerolled)
            {
                p.Seek(Local(Clip.Start), exact: true);
                _lastSeek = Local(Clip.Start);
                _prerolled = true;
            }
            return;
        }
        _prerolled = false;
        var local = Local(t);
        var pos = p.TimePos;

        if (playing)
        {
            if (p.Paused)
            {
                if (Math.Abs(pos - local) > 0.04) p.Seek(local, exact: true);
                p.SetPause(false);
                _lastSync = now;
            }
            else if (Math.Abs(pos - local) > 0.15 && (now - _lastSync).TotalSeconds > 0.7
                     && !(Clip.Kind == ClipKind.Gif && local < 0.3))
            {
                p.Seek(local, exact: true);
                _lastSync = now;
            }
        }
        else
        {
            if (!p.Paused) p.SetPause(true);
            if (seeked || Math.Abs(local - _lastSeek) > 0.01 && Math.Abs(pos - local) > 0.02)
            {
                p.Seek(local, exact: true);
                _lastSeek = local;
            }
        }
    }

    // render thread
    private void OnFrame(IntPtr data, int w, int h, int stride)
    {
        var place = _target;
        lock (_overlayLock)
        {
            if (_disposed) return;
            if (place == null || place.FullW != w || place.FullH != h || OverlayId <= 0)
            {
                return;
            }
            var ptr = data + place.SrcY * stride + place.SrcX * 4;
            if (_shownId != 0 && _shownId != OverlayId) _main.OverlayRemove(_shownId);
            _main.OverlayAdd(OverlayId, place.X, place.Y, ptr, place.W, place.H, stride);
            _shownId = OverlayId;
        }
    }

    private void HideOverlay()
    {
        lock (_overlayLock)
        {
            if (_shownId == 0) return;
            _main.OverlayRemove(_shownId);
            _shownId = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        HideOverlay();
        lock (_overlayLock) _disposed = true;
        var r = _renderer;
        var p = Player;
        _renderer = null;
        Player = null;
        // mpv teardown can take a moment; never on the ui thread
        _ = Task.Run(() =>
        {
            r?.Dispose();
            p?.Dispose();
        });
    }
}
