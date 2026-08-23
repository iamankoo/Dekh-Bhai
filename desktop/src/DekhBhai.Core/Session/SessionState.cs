namespace DekhBhai.Core.Session;

/// <summary>
/// Lifecycle of one broadcast. The host UI's "post-session" screen must only ever be shown
/// for <see cref="Stopped"/>, and only after every engine component has confirmed it actually
/// tore down - never merely because the user clicked stop.
/// </summary>
public enum SessionState
{
    Idle,
    Starting,
    Live,
    Stopping,
    Stopped,
    Error,
}

/// <summary>Why a session transitioned to <see cref="SessionState.Stopped"/>.</summary>
public enum SessionStopReason
{
    UserRequested,
    DurationExpired,
    FatalError,
}
