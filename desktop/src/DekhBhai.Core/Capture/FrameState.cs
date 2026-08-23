namespace DekhBhai.Core.Capture;

/// <summary>
/// Health state of a single captured frame or capture-pipeline event.
/// A black or unavailable frame is a normal, expected media state - never a
/// failure condition on its own. See docs/architecture/phase-1-technology-decision.md
/// for the reasoning behind this distinction.
/// </summary>
public enum FrameState
{
    /// <summary>Frame delivered normally by the capture API. Content may legitimately be black.</summary>
    ValidFrame,

    /// <summary>The capture API produced no new frame within the expected interval (e.g. during a resize).</summary>
    TemporarilyUnavailable,

    /// <summary>The capture pipeline threw while producing a frame; the pipeline attempts to keep running.</summary>
    CaptureError,

    /// <summary>Capture stopped because the user explicitly stopped sharing.</summary>
    UserStopped,
}

/// <summary>
/// Cheap, informational-only classification of a <see cref="FrameState.ValidFrame"/>'s content,
/// shown on the host status line so the operator can tell "protected content is rendering black"
/// apart from "the encoder died". It never changes control flow: a black frame is still
/// encoded and transmitted exactly like any other frame.
/// </summary>
public enum FrameContentHint
{
    Normal,
    LikelyBlack,
}
