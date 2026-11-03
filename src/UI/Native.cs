using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Cut.UI;

internal static class Native
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // dark title bar + caption colour on windows 10 1809+ / 11
    public static void DarkTitleBar(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
            int caption = 0x0010_0E0E; // COLORREF bgr of #0E0E10
            DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
        }
        catch
        {
            // older windows: default title bar
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    // the visible window bounds in physical pixels (no invisible resize borders)
    public static RECT VisibleBounds(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        DwmGetWindowAttribute(hwnd, 9, out var r, Marshal.SizeOf<RECT>());
        return r;
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hwnd, bool enable);

    // mpv draws into its own child window inside ours. disabling it makes the system skip it
    // for mouse hit testing, so clicks fall through our transparent static host to wpf, and
    // keyboard focus can never land on mpv's thread.
    public static int DisableChildren(IntPtr parent)
    {
        int n = 0;
        EnumChildWindows(parent, (h, _) =>
        {
            EnableWindow(h, false);
            n++;
            return true;
        }, IntPtr.Zero);
        return n;
    }

    // ---- synthetic input, used only by the --demo guide recorder ----

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy, mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public short wVk, wScan;
        public int dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    private const int MOUSEEVENTF_LEFTDOWN = 0x02, MOUSEEVENTF_LEFTUP = 0x04;
    private const int MOUSEEVENTF_RIGHTDOWN = 0x08, MOUSEEVENTF_RIGHTUP = 0x10;
    private const int MOUSEEVENTF_WHEEL = 0x800;
    private const int KEYEVENTF_KEYUP = 0x2;

    private static void Send(INPUT i) => SendInput(1, new[] { i }, Marshal.SizeOf<INPUT>());

    public static void MouseButton(bool left, bool down)
    {
        var flag = left ? (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP) : (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP);
        Send(new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flag } });
    }

    public static void Wheel(int delta) => Send(new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = delta } });

    public static void Key(int vk, bool down) =>
        Send(new INPUT { type = 1, ki = new KEYBDINPUT { wVk = (short)vk, dwFlags = down ? 0 : KEYEVENTF_KEYUP } });
}
