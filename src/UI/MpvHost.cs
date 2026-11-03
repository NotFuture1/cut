using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cut.UI;

// hosts a plain win32 child window inside WPF; its HWND is handed to mpv as wid.
// nothing wpf can draw over this area (airspace), so layers and selection outlines are
// drawn by mpv itself as osd overlays. the static class is hit-test transparent, and
// mpv's own child window gets disabled once it exists, so mouse input reaches wpf.
public sealed class MpvHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int SS_BLACKRECT = 0x00000004;

    private IntPtr _hwnd;
    private readonly TaskCompletionSource<long> _created = new();

    // completes once the child HWND exists
    public Task<long> HwndReady => _created.Task;
    public IntPtr Hwnd => _hwnd;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowExW(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | SS_BLACKRECT,
            0, 0, 100, 100, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW failed ({Marshal.GetLastWin32Error()})");
        _created.TrySetResult(_hwnd.ToInt64());
        return new HandleRef(this, _hwnd);
    }

    public int ReleaseInput() => _hwnd == IntPtr.Zero ? 0 : Native.DisableChildren(_hwnd);

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }
}
