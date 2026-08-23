namespace DekhBhai.App;

/// <summary>
/// Where the host talks to signaling. Phase 1 only ever runs this against a local development
/// signaling server, but the endpoint is never hard-coded into the session/session-controller
/// layer - it's read here, at the composition root, specifically so Phase 2 can point the same
/// app at a public production signaling service (e.g. wss://cast.dekhbhai.app/ws) by setting
/// environment variables, with no code changes required.
/// </summary>
internal static class AppConfig
{
    private const string DefaultSignalingWsUrl = "ws://localhost:8787/ws?role=host";
    private const string DefaultViewerBaseUrl = "http://localhost:8787/";

    /// <summary>
    /// WebSocket URL (including the `role=host` query param the signaling server expects) the
    /// host connects to. Override with the DEKHBHAI_SIGNALING_WS_URL environment variable -
    /// e.g. "wss://cast.example.com/ws?role=host" for a production deployment.
    /// </summary>
    public static Uri SignalingWsUrl =>
        new(Environment.GetEnvironmentVariable("DEKHBHAI_SIGNALING_WS_URL") ?? DefaultSignalingWsUrl);

    /// <summary>
    /// Base HTTP(S) URL the generated viewer share link is built from
    /// (`{ViewerBaseUrl}?session={id}`). Override with DEKHBHAI_VIEWER_BASE_URL.
    /// </summary>
    public static Uri ViewerBaseUrl =>
        new(Environment.GetEnvironmentVariable("DEKHBHAI_VIEWER_BASE_URL") ?? DefaultViewerBaseUrl);
}
