using DekhBhai.Core.Capture;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace DekhBhai.Core.Media;

/// <summary>
/// Bridges the screen capture engine to a VP8 encoder and hands finished access units to a
/// caller-supplied sink (normally <see cref="Rtc.WebRtcHost"/>'s SendVideo calls). Frame-rate
/// pacing lives here, not in the capture engine: WGC delivers frames at whatever rate the
/// desktop actually repaints, so this is where we drop frames down to the configured target.
/// </summary>
public sealed class VideoEncoderPipeline : IDisposable
{
    public const int RtpClockRate = 90000;

    /// <summary>Encoded VP8 access unit + its RTP duration in 90kHz units.</summary>
    public event Action<byte[], uint>? EncodedSampleReady;

    /// <summary>Passes through non-frame capture pipeline events (errors, stalls, stop).</summary>
    public event EventHandler<CaptureEvent>? CaptureStateChanged;

    private readonly IScreenCapture _capture;
    private readonly FFmpegVideoEncoder _encoder;
    private readonly TimeSpan _minFrameInterval;
    private DateTimeOffset _lastEncodedAt = DateTimeOffset.MinValue;
    private byte[]? _packedBuffer;

    public VideoEncoderPipeline(IScreenCapture capture, int targetFramesPerSecond)
    {
        _capture = capture;
        _encoder = new FFmpegVideoEncoder();
        _minFrameInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, targetFramesPerSecond));
        _capture.FrameArrived += OnCaptureEvent;
    }

    private void OnCaptureEvent(object? sender, CaptureEvent evt)
    {
        if (evt.State != FrameState.ValidFrame || evt.Frame is not { } frame)
        {
            CaptureStateChanged?.Invoke(this, evt);
            return;
        }

        var now = frame.Timestamp;
        if (_lastEncodedAt != DateTimeOffset.MinValue && now - _lastEncodedAt < _minFrameInterval)
        {
            return; // pacing: drop frames arriving faster than the configured target FPS
        }

        var elapsed = _lastEncodedAt == DateTimeOffset.MinValue
            ? TimeSpan.FromSeconds(1.0 / 30)
            : now - _lastEncodedAt;
        _lastEncodedAt = now;

        try
        {
            var packed = PackTightly(frame);
            var encoded = _encoder.EncodeVideo(
                frame.Width,
                frame.Height,
                packed,
                VideoPixelFormatsEnum.Bgra,
                VideoCodecsEnum.VP8);

            if (encoded is { Length: > 0 })
            {
                uint durationRtpUnits = (uint)Math.Max(1, elapsed.TotalSeconds * RtpClockRate);
                EncodedSampleReady?.Invoke(encoded, durationRtpUnits);
            }
        }
        catch (Exception ex)
        {
            CaptureStateChanged?.Invoke(this, CaptureEvent.ForState(FrameState.CaptureError, $"video encode failed: {ex.Message}"));
        }
    }

    /// <summary>Removes GPU row-alignment padding so the encoder sees a tightly packed BGRA buffer.</summary>
    private byte[] PackTightly(CapturedFrame frame)
    {
        int tightStride = frame.Width * 4;
        if (frame.Stride == tightStride)
        {
            return frame.Data.ToArray();
        }

        _packedBuffer = _packedBuffer?.Length == tightStride * frame.Height
            ? _packedBuffer
            : new byte[tightStride * frame.Height];

        var src = frame.Data.Span;
        for (int y = 0; y < frame.Height; y++)
        {
            src.Slice(y * frame.Stride, tightStride).CopyTo(_packedBuffer.AsSpan(y * tightStride, tightStride));
        }
        return _packedBuffer;
    }

    public void Dispose()
    {
        _capture.FrameArrived -= OnCaptureEvent;
        _encoder.Dispose();
    }
}
