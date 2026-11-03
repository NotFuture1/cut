using System.Reflection;
using System.Runtime.InteropServices;

namespace Cut.Core;

// thin P/Invoke surface over libmpv-2.dll (must sit next to cut.exe).
// libmpv is UTF-8 only: every string crossing the boundary is LPUTF8Str.
internal static class MpvNative
{
    private const string Lib = "libmpv-2";

    // mpv_event_id (client.h)
    public const int EVENT_NONE = 0;
    public const int EVENT_SHUTDOWN = 1;
    public const int EVENT_LOG_MESSAGE = 2;
    public const int EVENT_START_FILE = 6;
    public const int EVENT_END_FILE = 7;
    public const int EVENT_FILE_LOADED = 8;
    public const int EVENT_PLAYBACK_RESTART = 21;
    public const int EVENT_PROPERTY_CHANGE = 22;

    // mpv_format
    public const int FORMAT_NONE = 0;
    public const int FORMAT_STRING = 1;
    public const int FORMAT_FLAG = 3;
    public const int FORMAT_INT64 = 4;
    public const int FORMAT_DOUBLE = 5;

    // mpv_render_param_type (render.h)
    public const int RENDER_PARAM_INVALID = 0;
    public const int RENDER_PARAM_API_TYPE = 1;
    public const int RENDER_PARAM_SW_SIZE = 17;
    public const int RENDER_PARAM_SW_FORMAT = 18;
    public const int RENDER_PARAM_SW_STRIDE = 19;
    public const int RENDER_PARAM_SW_POINTER = 20;
    public const ulong RENDER_UPDATE_FRAME = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventProperty
    {
        public IntPtr Name; // char* (utf-8)
        public int Format;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EndFile
    {
        public int Reason;
        public int Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenderParam
    {
        public int Type;
        public IntPtr Data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void RenderUpdateFn(IntPtr ctx);

    // dev runs off windows load the system libmpv (brew) under the windows dll name
    private static int _resolverSet;
    public static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverSet, 1) == 1 || OperatingSystem.IsWindows()) return;
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, asm, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            foreach (var candidate in new[]
                     {
                         Environment.GetEnvironmentVariable("CUT_LIBMPV") ?? "",
                         "/opt/homebrew/lib/libmpv.dylib", "/usr/local/lib/libmpv.dylib", "libmpv.so.2", "libmpv.so",
                     })
            {
                if (candidate.Length > 0 && NativeLibrary.TryLoad(candidate, out var h)) return h;
            }
            return IntPtr.Zero;
        });
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    // used for wid (FORMAT_INT64)
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, ref long data);

    // argv: pointer to NULL-terminated array of utf-8 string pointers (marshaled by hand)
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr handle, IntPtr argv);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command_async(IntPtr handle, ulong replyUserdata, IntPtr argv);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    // returns char* that must be released with mpv_free (IntPtr.Zero if unavailable)
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_get_property_string(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_observe_property(IntPtr handle, ulong userdata,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr handle, double timeoutSec);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_wakeup(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_error_string(int code);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(IntPtr data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_create(out IntPtr ctx, IntPtr mpv, [In] RenderParam[] parameters);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_set_update_callback(IntPtr ctx, RenderUpdateFn? callback, IntPtr callbackCtx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong mpv_render_context_update(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_render(IntPtr ctx, [In] RenderParam[] parameters);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_free(IntPtr ctx);

    public static string ErrorString(int code)
    {
        var p = mpv_error_string(code);
        return Marshal.PtrToStringUTF8(p) ?? $"mpv error {code}";
    }
}
