using System.Runtime.InteropServices;

namespace Cut.Core;

// libmpv software render api: draws the current frame of a Render-role player into our
// own memory, so a video layer can be handed to the preview as an osd overlay with alpha.
// all rendering happens on one private thread; Frame fires there with premultiplied bgra.
public sealed unsafe class MpvSwRenderer : IDisposable
{
    private IntPtr _ctx;
    private readonly MpvNative.RenderUpdateFn _callback; // must outlive the context
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _disposed;

    private volatile int _reqW, _reqH;
    private volatile float _opacity = 1;
    private int _w, _h, _stride;
    private readonly IntPtr[] _buf = new IntPtr[2];
    private int _cur;
    private bool _force;

    private readonly IntPtr _sizeParam = Marshal.AllocHGlobal(8);
    private readonly IntPtr _strideParam = Marshal.AllocHGlobal(8);
    private readonly IntPtr _formatParam = Marshal.StringToHGlobalAnsi("bgr0");

    // ptr, width, height, stride (bytes). the buffer is reused two frames later.
    public event Action<IntPtr, int, int, int>? Frame;
    public int FramesRendered { get; private set; }

    public MpvSwRenderer(MpvPlayer player)
    {
        var api = Marshal.StringToHGlobalAnsi("sw");
        try
        {
            var p = new[]
            {
                new MpvNative.RenderParam { Type = MpvNative.RENDER_PARAM_API_TYPE, Data = api },
                new MpvNative.RenderParam { Type = MpvNative.RENDER_PARAM_INVALID, Data = IntPtr.Zero },
            };
            var rc = MpvNative.mpv_render_context_create(out _ctx, player.Handle, p);
            if (rc < 0) throw new InvalidOperationException($"mpv sw render: {MpvNative.ErrorString(rc)}");
        }
        finally
        {
            Marshal.FreeHGlobal(api);
        }
        _callback = _ => _wake.Set();
        MpvNative.mpv_render_context_set_update_callback(_ctx, _callback, IntPtr.Zero);
        _thread = new Thread(Loop) { IsBackground = true, Name = "mpv-sw-render" };
        _thread.Start();
    }

    public void SetTarget(int w, int h, double opacity)
    {
        w = Math.Clamp(w, 2, 4096);
        h = Math.Clamp(h, 2, 4096);
        var op = (float)Math.Clamp(opacity, 0, 1);
        if (w == _reqW && h == _reqH && Math.Abs(op - _opacity) < 0.001) return;
        _reqW = w;
        _reqH = h;
        _opacity = op;
        _force = true;
        _wake.Set();
    }

    public void Invalidate()
    {
        _force = true;
        _wake.Set();
    }

    private void Loop()
    {
        while (!_disposed)
        {
            _wake.WaitOne(500);
            if (_disposed) break;
            var flags = MpvNative.mpv_render_context_update(_ctx);
            if ((flags & MpvNative.RENDER_UPDATE_FRAME) == 0 && !_force) continue;
            _force = false;
            try { RenderOnce(); }
            catch (Exception ex) { Logger.Warn($"sw render: {ex.Message}"); }
        }
    }

    private void RenderOnce()
    {
        int w = _reqW, h = _reqH;
        if (w < 2 || h < 2) return;
        if (w != _w || h != _h)
        {
            Free();
            _w = w;
            _h = h;
            _stride = (w * 4 + 63) & ~63;
            for (int i = 0; i < 2; i++) _buf[i] = (IntPtr)NativeMemory.AlignedAlloc((nuint)(_stride * h), 64);
        }
        var target = _buf[_cur];
        Marshal.WriteInt32(_sizeParam, 0, w);
        Marshal.WriteInt32(_sizeParam, 4, h);
        Marshal.WriteInt64(_strideParam, _stride);
        var p = new[]
        {
            new MpvNative.RenderParam { Type = MpvNative.RENDER_PARAM_SW_SIZE, Data = _sizeParam },
            new MpvNative.RenderParam { Type = MpvNative.RENDER_PARAM_SW_FORMAT, Data = _formatParam },
            new MpvNative.RenderParam { Type = MpvNative.RENDER_PARAM_SW_STRIDE, Data = _strideParam },
            new MpvNative.RenderParam { Type = MpvNative.RENDER_PARAM_SW_POINTER, Data = target },
            new MpvNative.RenderParam { Type = MpvNative.RENDER_PARAM_INVALID, Data = IntPtr.Zero },
        };
        var rc = MpvNative.mpv_render_context_render(_ctx, p);
        if (rc < 0)
        {
            Logger.Warn($"sw render failed: {MpvNative.ErrorString(rc)}");
            return;
        }
        Premultiply(target, w, h, _stride, _opacity);
        FramesRendered++;
        Frame?.Invoke(target, w, h, _stride);
        _cur ^= 1;
    }

    // bgr0 -> premultiplied bgra at the layer's opacity
    private static void Premultiply(IntPtr buf, int w, int h, int stride, float opacity)
    {
        var a = (uint)Math.Round(opacity * 255);
        for (int y = 0; y < h; y++)
        {
            var row = new Span<uint>((byte*)buf + (long)y * stride, w);
            if (a >= 255)
            {
                for (int x = 0; x < row.Length; x++) row[x] |= 0xFF000000u;
                continue;
            }
            for (int x = 0; x < row.Length; x++)
            {
                var px = row[x];
                var b = (px & 0xFF) * a / 255;
                var g = ((px >> 8) & 0xFF) * a / 255;
                var r = ((px >> 16) & 0xFF) * a / 255;
                row[x] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }
    }

    private void Free()
    {
        for (int i = 0; i < 2; i++)
        {
            if (_buf[i] != IntPtr.Zero) NativeMemory.AlignedFree((void*)_buf[i]);
            _buf[i] = IntPtr.Zero;
        }
    }

    // must run before the player is disposed
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        _thread.Join(2000);
        if (_ctx != IntPtr.Zero)
        {
            MpvNative.mpv_render_context_set_update_callback(_ctx, null, IntPtr.Zero);
            MpvNative.mpv_render_context_free(_ctx);
            _ctx = IntPtr.Zero;
        }
        Free();
        Marshal.FreeHGlobal(_sizeParam);
        Marshal.FreeHGlobal(_strideParam);
        Marshal.FreeHGlobal(_formatParam);
    }
}
