using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Editor;

/// <summary>
/// Leaves only Close in a dialog's title bar on Windows, or Maximize and Close for a resizable one.
/// Avalonia 11.3 creates every window with minimize and maximize boxes and has no property to drop them;
/// a minimized modal leaves the owner disabled.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WinDialogStyle
{
    private const int GwlStyle = -16;
    private const long WsMinimizeBox = 0x00020000, WsMaximizeBox = 0x00010000;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

    public static void RemoveMinMax(Window window, bool keepMaximize = false)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
        {
            CrashLog.Error("WinDialogStyle", $"no window handle for \"{window.Title}\"");
            return;
        }
        long style = GetStyle(hwnd);
        long trimmed = style & ~(WsMinimizeBox | (keepMaximize ? 0 : WsMaximizeBox));
        if (trimmed == style) return;
        SetStyle(hwnd, trimmed);
        // The frame is cached; this redraws the title bar without the boxes.
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private static long GetStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, GwlStyle).ToInt64() : GetWindowLong(hwnd, GwlStyle);

    private static void SetStyle(IntPtr hwnd, long style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));
        else SetWindowLong(hwnd, GwlStyle, (int)style);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
