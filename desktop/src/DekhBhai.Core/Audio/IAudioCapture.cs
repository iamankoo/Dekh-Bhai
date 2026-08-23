namespace DekhBhai.Core.Audio;

/// <summary>
/// One PCM audio buffer captured from a device, as interleaved IEEE-float samples in
/// [-1, 1] - the native WASAPI mix format, and also what the Opus encoder accepts directly.
/// </summary>
public readonly struct AudioChunk
{
    public required float[] Samples { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Continuous audio capture engine. Mirrors <see cref="Capture.IScreenCapture"/>: owns its own
/// capture thread and keeps running independent of UI state.
/// </summary>
public interface IAudioCapture : IDisposable
{
    event EventHandler<AudioChunk>? ChunkCaptured;
    event EventHandler<string>? CaptureError;

    bool IsCapturing { get; }

    void Start();
    void Stop();
}
