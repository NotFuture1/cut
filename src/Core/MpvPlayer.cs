using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Cut.Core;

// managed wrapper over one mpv handle. wid != null embeds video into that HWND;
// wid == null runs headless (vo=null/ao=null, used by --selftest).
public sealed class MpvPlayer : IDisposable
{
    private IntPtr _h;
    private Thread? _eventThread;
    private readonly Dispatcher? _ui;
    private bool _disposed;

    public event Action<double>? TimePosChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action? FileLoaded;
    public event Action? EndOfFile;

    public MpvPlayer(long? wid, Dispatcher? ui = null)
    {
        _ui = ui;
        _h = MpvNative.mpv_create();
        if (_h == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create returned null");

        if (wid is long w)
        {
            long widVal = w;
            Check(MpvNative.mpv_set_option(_h, "wid", MpvNative.FORMAT_INT64, ref widVal), "wid", fatal: true);
            SetOption("vo", "gpu");
            SetOption("hwdec", "auto-safe");
        }
        else
        {
            SetOption("vo", "null");
            SetOption("ao", "null");
        }

        SetOption("config", "no");
        SetOption("osc", "no");
        SetOption("input-default-bindings", "no");
        SetOption("input-vo-keyboard", "no");
        SetOption("keep-open", "always");
        SetOption("idle", "yes");
        SetOption("pause", "yes");
        SetOption("hr-seek", "yes");
        SetOption("drag-and-drop", "no");
        SetOption("background-color", "#0F0F0F");

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

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    private void SetOption(string name, string value)
        => Check(MpvNative.mpv_set_option_string(_h, name, value), $"option {name}={value}");

    private static void Check(int rc, string what, bool fatal = false)
    {
        if (rc >= 0) return;
        var msg = $"mpv {what}: {MpvNative.ErrorString(rc)}";
        if (fatal) throw new InvalidOperationException(msg);
        Logger.Warn(msg);
    }

    public int Command(params string[] args)
    {
        if (_h == IntPtr.Zero) return -1;
        var ptrs = new IntPtr[args.Length + 1]; // trailing NULL
        var argv = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Length);
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(args[i] + "\0");
                ptrs[i] = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, ptrs[i], bytes.Length);
            }
            Marshal.Copy(ptrs, 0, argv, ptrs.Length);
            var rc = MpvNative.mpv_command(_h, argv);
            if (rc < 0) Logger.Warn($"mpv command '{string.Join(' ', args)}' failed: {MpvNative.ErrorString(rc)}");
            return rc;
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal(argv);
        }
    }

    public void Load(string path) => Command("loadfile", path);

    public void SetPause(bool paused)
        => Check(MpvNative.mpv_set_property_string(_h, "pause", paused ? "yes" : "no"), "set pause");

    public void Seek(double seconds, bool exact)
        => Command("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            exact ? "absolute+exact" : "absolute+keyframes");

    public void FrameStep() => Command("frame-step");
    public void FrameBack() => Command("frame-back-step");

    // 1-based mpv audio id; follows container order = ffprobe audio stream order
    public void SetAudioTrack(int aid)
        => Check(MpvNative.mpv_set_property_string(_h, "aid",
            aid.ToString(CultureInfo.InvariantCulture)), "set aid");

    // 0..100
    public void SetVolume(double volume)
        => Check(MpvNative.mpv_set_property_string(_h, "volume",
            volume.ToString("0.#", CultureInfo.InvariantCulture)), "set volume");

    public string? GetPropertyString(string name)
    {
        if (_h == IntPtr.Zero) return null;
        var p = MpvNative.mpv_get_property_string(_h, name);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { MpvNative.mpv_free(p); }
    }

    public double? GetPropertyDouble(string name)
    {
        if (_h == IntPtr.Zero) return null;
        var rc = MpvNative.mpv_get_property(_h, name, MpvNative.FORMAT_DOUBLE, out var v);
        return rc >= 0 ? v : null;
    }

    private void EventLoop()
    {
        while (true)
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
                    Raise(() => FileLoaded?.Invoke());
                    break;
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
        if (name == null || prop.Data == IntPtr.Zero) return;

        switch (name)
        {
            case "time-pos" when prop.Format == MpvNative.FORMAT_DOUBLE:
            {
                var t = ReadDouble(prop.Data);
                Raise(() => TimePosChanged?.Invoke(t));
                break;
            }
            case "duration" when prop.Format == MpvNative.FORMAT_DOUBLE:
            {
                var d = ReadDouble(prop.Data);
                if (d > 0) Raise(() => DurationChanged?.Invoke(d));
                break;
            }
            case "pause" when prop.Format == MpvNative.FORMAT_FLAG:
            {
                var paused = Marshal.ReadInt32(prop.Data) != 0;
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

    private static double ReadDouble(IntPtr p)
    {
        var bits = Marshal.ReadInt64(p);
        return BitConverter.Int64BitsToDouble(bits);
    }

    private void Raise(Action action)
    {
        if (_disposed) return;
        if (_ui != null) _ui.BeginInvoke(action);
        else action();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Command("quit");
            _eventThread?.Join(3000);
        }
        catch (Exception ex)
        {
            Logger.Warn($"mpv shutdown: {ex.Message}");
        }
        if (_h != IntPtr.Zero)
        {
            MpvNative.mpv_terminate_destroy(_h);
            _h = IntPtr.Zero;
        }
    }
}
