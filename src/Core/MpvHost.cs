using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cut.Core;

// hosts a plain win32 child window inside WPF; its HWND is handed to mpv as wid.
// nothing may overlay this area (WPF airspace) - layout keeps controls outside it.
public sealed class MpvHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int SS_BLACKRECT = 0x00000004;

    private IntPtr _hwnd;
    private readonly TaskCompletionSource<long> _created = new();

    // completes once the child HWND exists
    public Task<long> HwndReady => _created.Task;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowExW(0, "static", "", WS_CHILD | WS_VISIBLE | SS_BLACKRECT,
            0, 0, 100, 100, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW failed ({Marshal.GetLastWin32Error()})");
        _created.TrySetResult(_hwnd.ToInt64());
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }
}
