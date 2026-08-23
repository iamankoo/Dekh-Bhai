namespace DekhBhai.Core.Capture;

/// <summary>
/// One BGRA32 frame copied out of the GPU capture pipeline into CPU memory, ready for the
/// video encoder. Ownership of <see cref="Data"/> belongs to the caller for the duration of
/// the FrameArrived callback only - it is a pooled/rented buffer that gets reused.
/// </summary>
public readonly struct CapturedFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Row stride in bytes (may be larger than Width * 4 due to GPU texture alignment).</summary>
    public required int Stride { get; init; }

    /// <summary>Tightly-packed or strided BGRA32 pixel data, length &gt;= Stride * Height.</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required FrameContentHint ContentHint { get; init; }
}

/// <summary>
/// A capture pipeline event: either a frame, or a non-fatal state transition
/// (see <see cref="FrameState"/>). Consumers should keep running for every state
/// except <see cref="FrameState.UserStopped"/>.
/// </summary>
public readonly struct CaptureEvent
{
    public required FrameState State { get; init; }
    public CapturedFrame? Frame { get; init; }
    public string? Message { get; init; }

    public static CaptureEvent ForFrame(CapturedFrame frame) =>
        new() { State = FrameState.ValidFrame, Frame = frame };

    public static CaptureEvent ForState(FrameState state, string? message = null) =>
        new() { State = state, Message = message };
}
