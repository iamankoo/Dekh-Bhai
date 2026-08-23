using System.Runtime.InteropServices;
using DekhBhai.Core.Capture.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace DekhBhai.Core.Capture;

/// <summary>
/// Continuous full-monitor capture using the Windows Graphics Capture API. Frames are
/// delivered on a dedicated background thread the WGC frame pool owns - this class never
/// touches the UI thread and keeps running regardless of the host window's state.
/// </summary>
public sealed class WindowsGraphicsScreenCapture : IScreenCapture
{
    public event EventHandler<CaptureEvent>? FrameArrived;

    public bool IsCapturing { get; private set; }

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _stagingTexture;
    private int _stagingWidth;
    private int _stagingHeight;
    private readonly object _frameLock = new();
    private int _consecutiveErrors;
    private FrameContentHint _lastHint = FrameContentHint.Normal;
    private DateTimeOffset _lastFrameAt = DateTimeOffset.MinValue;

    public void Start(CaptureSettings settings)
    {
        if (IsCapturing) return;

        var monitor = settings.MonitorHandle ?? MonitorHelper.GetPrimaryMonitor();
        Trace("monitor handle acquired");

        D3D11.D3D11CreateDevice(
            adapter: null,
            driverType: DriverType.Hardware,
            flags: DeviceCreationFlags.BgraSupport,
            featureLevels: null!,
            device: out _device,
            immediateContext: out _context).CheckError();
        Trace("d3d11 device created");

        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        Trace("dxgi device queried");
        var winrtDevice = GraphicsCaptureInterop.CreateDirect3DDeviceFromDxgiDevice(dxgiDevice);
        Trace("winrt device wrapped");

        _item = GraphicsCaptureInterop.CreateItemForMonitor(monitor);
        Trace("capture item created");
        _item.Closed += (_, _) => RaiseState(FrameState.TemporarilyUnavailable, "capture item closed by the system");

        var size = _item.Size;
        Trace($"item size {size.Width}x{size.Height}");
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            size);
        Trace("frame pool created");

        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);
        Trace("session created");

        try
        {
            _session.StartCapture();
            Trace("StartCapture called");
            IsCapturing = true;
            _consecutiveErrors = 0;
        }
        catch (Exception ex)
        {
            RaiseState(FrameState.CaptureError, $"failed to start capture: {ex.Message}");
            throw;
        }

        _ = TryDisableCaptureBorderAsync(_session);
    }

    /// <summary>
    /// Best-effort, fire-and-forget request to suppress Windows' own "capture in progress"
    /// border - see docs/architecture/phase-1-technology-decision.md for the full writeup.
    /// This only has any effect when the app has package identity (MSIX) and declares the
    /// graphicsCaptureWithoutBorder restricted capability; when run unpackaged (e.g. `dotnet
    /// run` during development) every step here fails harmlessly and the border behaves as
    /// before. Never allowed to affect capture correctness - only local display cosmetics.
    /// </summary>
    private static async Task TryDisableCaptureBorderAsync(GraphicsCaptureSession session)
    {
        try
        {
            // RequestAccessAsync itself returns the cached prior decision without re-prompting
            // if the user has already been asked - there is no separate "check without asking"
            // API on GraphicsCaptureAccess.
            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);

            if (status == global::Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed)
            {
                session.IsBorderRequired = false;
                Trace("borderless capture access granted; IsBorderRequired = false");
            }
            else
            {
                Trace($"borderless capture access not granted: {status}");
            }
        }
        catch (Exception ex)
        {
            // Expected when unpackaged/no restricted capability declared - not an error.
            Trace($"borderless capture request unavailable: {ex.Message}");
        }
    }

    internal static bool TraceEnabled = Environment.GetEnvironmentVariable("DEKHBHAI_TRACE") == "1";
    private static void Trace(string message)
    {
        if (!TraceEnabled) return;
        Console.WriteLine($"[trace] {message}");
        Console.Out.Flush();
    }

    public void Stop()
    {
        if (!IsCapturing) return;
        IsCapturing = false;

        try { _session?.Dispose(); } catch { /* best-effort teardown */ }
        try
        {
            if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;
            _framePool?.Dispose();
        }
        catch { /* best-effort teardown */ }
        try { _item = null; } catch { /* no-op */ }

        lock (_frameLock)
        {
            _stagingTexture?.Dispose();
            _stagingTexture = null;
        }

        _context?.Dispose();
        _device?.Dispose();
        _context = null;
        _device = null;

        RaiseState(FrameState.UserStopped);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!IsCapturing) return;

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                RaiseState(FrameState.TemporarilyUnavailable, "no frame available this interval");
                return;
            }

            HandleSizeChangeIfNeeded(sender, frame.ContentSize);
            ProcessFrame(frame);
            _consecutiveErrors = 0;
        }
        catch (Exception ex)
        {
            _consecutiveErrors++;
            RaiseState(FrameState.CaptureError, ex.Message);
            // A handful of consecutive failures is tolerated (matches the "black frame /
            // transient unavailability is not a failure" requirement); only stop the whole
            // engine if the pipeline is persistently broken.
            if (_consecutiveErrors > 30)
            {
                Stop();
            }
        }
    }

    private void HandleSizeChangeIfNeeded(Direct3D11CaptureFramePool pool, Windows.Graphics.SizeInt32 contentSize)
    {
        if (_item is null || _device is null) return;
        if (contentSize.Width == _item.Size.Width && contentSize.Height == _item.Size.Height) return;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        var winrtDevice = GraphicsCaptureInterop.CreateDirect3DDeviceFromDxgiDevice(dxgiDevice);
        pool.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
    }

    private unsafe void ProcessFrame(Direct3D11CaptureFrame frame)
    {
        if (_context is null || _device is null) return;

        using var sourceTexture = GraphicsCaptureInterop.GetTexture(frame.Surface);
        var desc = sourceTexture.Description;

        int width = (int)desc.Width;
        int height = (int)desc.Height;

        lock (_frameLock)
        {
            EnsureStagingTexture(width, height);
            _context.CopyResource(_stagingTexture!, sourceTexture);

            var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int stride = (int)mapped.RowPitch;
                int size = stride * height;

                var buffer = new byte[size];
                var src = new ReadOnlySpan<byte>((void*)mapped.DataPointer, size);
                src.CopyTo(buffer);

                var hint = SampleContentHint(buffer, stride, width, height);
                var now = DateTimeOffset.UtcNow;
                if (hint != _lastHint)
                {
                    var gapMs = _lastFrameAt == DateTimeOffset.MinValue ? 0 : (now - _lastFrameAt).TotalMilliseconds;
                    Trace($"content hint changed: {_lastHint} -> {hint} (gap since previous frame: {gapMs:F0}ms)");
                    _lastHint = hint;
                }
                _lastFrameAt = now;

                var captured = new CapturedFrame
                {
                    Width = width,
                    Height = height,
                    Stride = stride,
                    Data = buffer,
                    Timestamp = DateTimeOffset.UtcNow,
                    ContentHint = hint,
                };
                FrameArrived?.Invoke(this, CaptureEvent.ForFrame(captured));
            }
            finally
            {
                _context.Unmap(_stagingTexture!, 0);
            }
        }
    }

    /// <summary>Cheap, informational-only sampling - see <see cref="FrameContentHint"/>.</summary>
    internal static FrameContentHint SampleContentHint(byte[] buffer, int stride, int width, int height)
    {
        const int threshold = 8;
        int[] xs = { 0, width / 2, Math.Max(0, width - 1) };
        int[] ys = { 0, height / 2, Math.Max(0, height - 1) };

        foreach (var y in ys)
        {
            foreach (var x in xs)
            {
                int offset = y * stride + x * 4;
                if (offset + 2 >= buffer.Length) continue;
                if (buffer[offset] > threshold || buffer[offset + 1] > threshold || buffer[offset + 2] > threshold)
                {
                    return FrameContentHint.Normal;
                }
            }
        }
        return FrameContentHint.LikelyBlack;
    }

    private void EnsureStagingTexture(int width, int height)
    {
        if (_stagingTexture is not null && _stagingWidth == width && _stagingHeight == height) return;

        _stagingTexture?.Dispose();
        _stagingTexture = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
        _stagingWidth = width;
        _stagingHeight = height;
    }

    private void RaiseState(FrameState state, string? message = null) =>
        FrameArrived?.Invoke(this, CaptureEvent.ForState(state, message));

    public void Dispose() => Stop();
}
