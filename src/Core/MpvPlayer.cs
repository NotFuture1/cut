using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Cut.Core;

public enum MpvRole
{
    Embedded, // video into a child hwnd (the preview)
    Headless, // no video, no audio output (selftest)
    Audio,    // sound only (music / clip audio in preview)
    Render,   // software render api + sound (video overlays in preview)
}

// managed wrapper over one mpv handle. events are marshalled through `post` (the ui
// dispatcher in the app, inline in tests). time-pos is coalesced: a burst of updates
// while the ui is busy turns into one callback with the latest value.
public sealed class MpvPlayer : IDisposable
{
    private IntPtr _h;
    private Thread? _eventThread;
    private readonly Action<Action>? _post;
    private volatile bool _disposed;
    private double _latestTime;
    private int _timePending;

    public MpvRole Role { get; }
    public double TimePos => Volatile.Read(ref _latestTime);
    public double Duration { get; private set; }
    public bool Paused { get; private set; } = true;
    public bool Loaded { get; private set; }
    public string? LastError { get; private set; }

    public event Action<double>? TimePosChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action? FileLoaded;
    public event Action? EndOfFile;
    public event Action? OsdDimensionsChanged;
    public event Action? PlaybackRestart;

    internal IntPtr Handle => _h;

    static MpvPlayer() => MpvNative.EnsureResolver();

    public MpvPlayer(MpvRole role, long wid = 0, Action<Action>? post = null)
    {
        Role = role;
        _post = post;
        _h = MpvNative.mpv_create();
        if (_h == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create returned null");

        // nothing user-facing from mpv itself: no config, scripts, osd, bindings
        foreach (var (k, v) in new[]
                 {
                     ("config", "no"), ("terminal", "no"), ("osc", "no"), ("load-scripts", "no"), ("ytdl", "no"),
                     ("load-stats-overlay", "no"), ("load-console", "no"), ("load-auto-profiles", "no"),
                     ("load-select", "no"), ("load-commands", "no"), ("load-context-menu", "no"),
                     ("load-positioning", "no"), ("input-default-bindings", "no"), ("input-vo-keyboard", "no"),
                     ("input-cursor", "no"), ("cursor-autohide", "no"), ("osd-level", "0"),
                     ("audio-file-auto", "no"), ("sub-auto", "no"), ("keep-open", "always"), ("idle", "yes"),
                     ("pause", "yes"), ("hr-seek", "yes"), ("hr-seek-framedrop", "yes"), ("drag-and-drop", "no"),
                     ("cache", "yes"), ("demuxer-readahead-secs", "8"), ("demuxer-max-back-bytes", "96MiB"),
                     ("volume-max", "200"), ("audio-display", "no"),
                 })
            SetOptionQuiet(k, v);

        switch (role)
        {
            case MpvRole.Embedded:
                long widVal = wid;
                Check(MpvNative.mpv_set_option(_h, "wid", MpvNative.FORMAT_INT64, ref widVal), "wid", fatal: true);
                SetOption("vo", "gpu");
                SetOption("hwdec", "auto-safe");
                SetOption("background-color", "#0B0B0C");
                break;
            case MpvRole.Headless:
                SetOption("vo", "null");
                SetOption("ao", "null");
                break;
            case MpvRole.Audio:
                SetOption("vo", "null");
                SetOption("vid", "no");
                break;
            case MpvRole.Render:
                SetOption("vo", "libmpv");
                SetOption("hwdec", "no");
                break;
        }
        if (Environment.GetEnvironmentVariable("CUT_MPV_NOAUDIO") == "1") SetOptionQuiet("ao", "null");

        var rc = MpvNative.mpv_initialize(_h);
        if (rc < 0)
        {
            MpvNative.mpv_terminate_destroy(_h);
            _h = IntPtr.Zero;
            throw new InvalidOperationException($"mpv_initialize failed: {MpvNative.ErrorString(rc)}");
        }

        Check(MpvNative.mpv_observe_property(_h, 1, "time-pos", MpvNative.FORMAT_DOUBLE), "observe time-pos");
        Check(MpvNative.mpv_observe_property(_h, 2, "duration", MpvNative.FORMAT_DOUBLE), "observe duration");
        Check(MpvNative.mpv_observe_property(_h, 3, "pause", MpvNative.FORMAT_FLAG), "observe pause");
        Check(MpvNative.mpv_observe_property(_h, 4, "eof-reached", MpvNative.FORMAT_FLAG), "observe eof-reached");
        if (role == MpvRole.Embedded)
            Check(MpvNative.mpv_observe_property(_h, 5, "osd-dimensions", MpvNative.FORMAT_NONE), "observe osd-dimensions");

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = $"mpv-{role}" };
        _eventThread.Start();
    }

    private void SetOption(string name, string value)
        => Check(MpvNative.mpv_set_option_string(_h, name, value), $"option {name}={value}");

    // options that newer/older libmpv builds may not know; failing is fine
    private void SetOptionQuiet(string name, string value) => MpvNative.mpv_set_option_string(_h, name, value);

    private static void Check(int rc, string what, bool fatal = false)
    {
        if (rc >= 0) return;
        var msg = $"mpv {what}: {MpvNative.ErrorString(rc)}";
        if (fatal) throw new InvalidOperationException(msg);
        Logger.Warn(msg);
    }

    private static IntPtr BuildArgv(string[] args, out IntPtr[] ptrs)
    {
        ptrs = new IntPtr[args.Length + 1]; // trailing NULL
        var argv = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Length);
        for (int i = 0; i < args.Length; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(args[i] + "\0");
            ptrs[i] = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptrs[i], bytes.Length);
        }
        Marshal.Copy(ptrs, 0, argv, ptrs.Length);
        return argv;
    }

    private static void FreeArgv(IntPtr argv, IntPtr[] ptrs)
    {
        foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        Marshal.FreeHGlobal(argv);
    }

    public int Command(params string[] args)
    {
        if (_h == IntPtr.Zero || _disposed) return -1;
        var argv = BuildArgv(args, out var ptrs);
        try
        {
            var rc = MpvNative.mpv_command(_h, argv);
            if (rc < 0)
            {
                LastError = MpvNative.ErrorString(rc);
                Logger.Warn($"mpv command '{string.Join(' ', args.Take(3))}' failed: {LastError}");
            }
            return rc;
        }
        finally
        {
            FreeArgv(argv, ptrs);
        }
    }

    // fire and forget: seeks while scrubbing must never block the ui thread
    public void CommandAsync(params string[] args)
    {
        if (_h == IntPtr.Zero || _disposed) return;
        var argv = BuildArgv(args, out var ptrs);
        try { MpvNative.mpv_command_async(_h, 0, argv); }
        finally { FreeArgv(argv, ptrs); }
    }

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public void Load(string path)
    {
        Loaded = false;
        Duration = 0;
        Command("loadfile", path);
    }

    public void SetPause(bool paused) => SetProperty("pause", paused ? "yes" : "no");

    public void Seek(double seconds, bool exact)
        => CommandAsync("seek", F(Math.Max(0, seconds)), exact ? "absolute+exact" : "absolute+keyframes");

    public void FrameStep() => Command("frame-step");
    public void FrameBack() => Command("frame-back-step");

    // 0..200
    public void SetVolume(double volume) => SetProperty("volume", F(Math.Clamp(volume, 0, 200)));

    public bool SetProperty(string name, string value)
    {
        if (_h == IntPtr.Zero || _disposed) return false;
        var rc = MpvNative.mpv_set_property_string(_h, name, value);
        if (rc < 0)
        {
            LastError = MpvNative.ErrorString(rc);
            Logger.Warn($"mpv set {name}: {LastError}");
        }
        return rc >= 0;
    }

    public string? GetPropertyString(string name)
    {
        if (_h == IntPtr.Zero || _disposed) return null;
        var p = MpvNative.mpv_get_property_string(_h, name);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { MpvNative.mpv_free(p); }
    }

    public double? GetPropertyDouble(string name)
    {
        if (_h == IntPtr.Zero || _disposed) return null;
        var rc = MpvNative.mpv_get_property(_h, name, MpvNative.FORMAT_DOUBLE, out var v);
        return rc >= 0 ? v : null;
    }

    // ---- osd overlays (images and video-layer frames drawn by mpv over the video) ----

    // data: premultiplied bgra, stride in bytes. mpv copies it before returning.
    public void OverlayAdd(int id, int x, int y, IntPtr data, int w, int h, int stride)
        => Command("overlay-add", id.ToString(CultureInfo.InvariantCulture),
            x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture),
            "&" + data.ToInt64().ToString(CultureInfo.InvariantCulture), "0", "bgra",
            w.ToString(CultureInfo.InvariantCulture), h.ToString(CultureInfo.InvariantCulture),
            stride.ToString(CultureInfo.InvariantCulture));

    public void OverlayRemove(int id) => Command("overlay-remove", id.ToString(CultureInfo.InvariantCulture));

    // where the video sits inside the mpv window, in physical pixels
    public (int X, int Y, int W, int H)? VideoRect()
    {
        int G(string n) => int.TryParse(GetPropertyString("osd-dimensions/" + n), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var v) ? v : 0;
        var w = G("w");
        var h = G("h");
        if (w <= 0 || h <= 0) return null;
        int ml = G("ml"), mr = G("mr"), mt = G("mt"), mb = G("mb");
        return (ml, mt, Math.Max(1, w - ml - mr), Math.Max(1, h - mt - mb));
    }

    private void EventLoop()
    {
        while (!_disposed)
        {
            IntPtr evPtr;
            try { evPtr = MpvNative.mpv_wait_event(_h, 0.5); }
            catch { break; }
            if (evPtr == IntPtr.Zero) continue;

            var ev = Marshal.PtrToStructure<MpvNative.Event>(evPtr);
            switch (ev.EventId)
            {
                case MpvNative.EVENT_NONE:
                    continue;
                case MpvNative.EVENT_SHUTDOWN:
                    return;
                case MpvNative.EVENT_FILE_LOADED:
                    Loaded = true;
                    Raise(() => FileLoaded?.Invoke());
                    break;
                case MpvNative.EVENT_PLAYBACK_RESTART:
                    Raise(() => PlaybackRestart?.Invoke());
                    break;
                case MpvNative.EVENT_END_FILE when ev.Data != IntPtr.Zero:
                {
                    var ef = Marshal.PtrToStructure<MpvNative.EndFile>(ev.Data);
                    if (ef.Error < 0) LastError = MpvNative.ErrorString(ef.Error);
                    break;
                }
                case MpvNative.EVENT_PROPERTY_CHANGE:
                    HandlePropertyChange(ev);
                    break;
            }
        }
    }

    private void HandlePropertyChange(MpvNative.Event ev)
    {
        if (ev.Data == IntPtr.Zero) return;
        var prop = Marshal.PtrToStructure<MpvNative.EventProperty>(ev.Data);
        var name = Marshal.PtrToStringUTF8(prop.Name);
        if (name == "osd-dimensions")
        {
            Raise(() => OsdDimensionsChanged?.Invoke());
            return;
        }
        if (name == null || prop.Data == IntPtr.Zero) return;

        switch (name)
        {
            case "time-pos" when prop.Format == MpvNative.FORMAT_DOUBLE:
            {
                Volatile.Write(ref _latestTime, ReadDouble(prop.Data));
                if (Interlocked.Exchange(ref _timePending, 1) == 0)
                    Raise(() =>
                    {
                        Interlocked.Exchange(ref _timePending, 0);
                        TimePosChanged?.Invoke(TimePos);
                    });
                break;
            }
            case "duration" when prop.Format == MpvNative.FORMAT_DOUBLE:
            {
                var d = ReadDouble(prop.Data);
                if (d > 0)
                {
                    Duration = d;
                    Raise(() => DurationChanged?.Invoke(d));
                }
                break;
            }
            case "pause" when prop.Format == MpvNative.FORMAT_FLAG:
            {
                var paused = Marshal.ReadInt32(prop.Data) != 0;
                Paused = paused;
                Raise(() => PauseChanged?.Invoke(paused));
                break;
            }
            case "eof-reached" when prop.Format == MpvNative.FORMAT_FLAG:
            {
                if (Marshal.ReadInt32(prop.Data) != 0) Raise(() => EndOfFile?.Invoke());
                break;
            }
        }
    }

    private static double ReadDouble(IntPtr p) => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p));

    private void Raise(Action action)
    {
        if (_disposed) return;
        if (_post != null) _post(() => { if (!_disposed) action(); });
        else action();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_h == IntPtr.Zero) return;
        try
        {
            MpvNative.mpv_wakeup(_h);
            _eventThread?.Join(2000);
        }
        catch (Exception ex)
        {
            Logger.Warn($"mpv shutdown: {ex.Message}");
        }
        MpvNative.mpv_terminate_destroy(_h);
        _h = IntPtr.Zero;
    }
}
