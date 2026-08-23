namespace DekhBhai.Core.Capture;

/// <summary>
/// Continuous screen capture engine. Implementations must not couple to any UI: capture
/// lifetime is controlled purely by Start/Stop calls and must keep running independent of
/// window visibility, focus, or minimize state.
/// </summary>
public interface IScreenCapture : IDisposable
{
    /// <summary>Raised for every captured frame and every non-fatal pipeline state change.</summary>
    event EventHandler<CaptureEvent>? FrameArrived;

    bool IsCapturing { get; }

    /// <summary>
    /// Starts capturing the given monitor at the requested size/frame rate. The capture
    /// engine owns its own thread and does not block the caller.
    /// </summary>
    void Start(CaptureSettings settings);

    void Stop();
}

/// <summary>Configurable capture parameters. Resolution/FPS are hints, not hard guarantees.</summary>
public sealed class CaptureSettings
{
    /// <summary>Win32 HMONITOR handle of the display to capture. Null selects the primary display.</summary>
    public nint? MonitorHandle { get; init; }

    public int TargetWidth { get; init; } = 1920;
    public int TargetHeight { get; init; } = 1080;
    public int TargetFramesPerSecond { get; init; } = 30;
}
