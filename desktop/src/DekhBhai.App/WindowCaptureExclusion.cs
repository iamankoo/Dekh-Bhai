using System.Runtime.InteropServices;

namespace DekhBhai.App;

/// <summary>
/// Wraps SetWindowDisplayAffinity so the Dekh Bhai control window can never appear in any
/// screen capture (ours or anyone else's) even if the user restores it from the taskbar while
/// a stream is live. This is belt-and-suspenders alongside minimizing the window on start -
/// see the "host UI must not be captured" requirement in the project brief.
/// </summary>
internal static class WindowCaptureExclusion
{
    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    /// <summary>Requires Windows 10 2004+; silently no-ops on older builds.</summary>
    public static void Exclude(IntPtr hWnd) => SetWindowDisplayAffinity(hWnd, WDA_EXCLUDEFROMCAPTURE);

    public static void Restore(IntPtr hWnd) => SetWindowDisplayAffinity(hWnd, WDA_NONE);
}
