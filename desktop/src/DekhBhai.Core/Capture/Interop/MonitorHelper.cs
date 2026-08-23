using System.Runtime.InteropServices;

namespace DekhBhai.Core.Capture.Interop;

internal static class MonitorHelper
{
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    /// <summary>HMONITOR of the current primary display, without needing a window handle.</summary>
    public static nint GetPrimaryMonitor() => MonitorFromWindow(IntPtr.Zero, MONITOR_DEFAULTTOPRIMARY);
}
