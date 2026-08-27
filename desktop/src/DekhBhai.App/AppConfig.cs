using System.Net;

namespace DekhBhai.App;

/// <summary>
/// Where the host talks to signaling. The endpoint is never hard-coded into the session/
/// session-controller layer - it's read here, at the composition root - so a developer can
/// still point any build at a different deployment via environment variables.
///
/// The *default* differs by build configuration:
/// - Debug (e.g. `dotnet run`): defaults to localhost, matching the local dev signaling server
///   started by `npm start` - see docs/development/setup.md.
/// - Release (what `scripts/build-msix.ps1` publishes, and therefore every installed MSIX):
///   defaults to the real production deployment. This exists specifically so a friend installing
///   the distributed app never needs to set an environment variable at all - see
///   docs/architecture/phase-3-technology-decision.md ("Release build now carries built-in
///   production defaults") for why this was necessary: every previous "it just works" test only
///   worked because something (a test harness, a manually-set env var) supplied these values -
///   a genuinely fresh install had no way to know them. Both values are public service endpoints
///   (a WSS URL and an HTTPS URL), never secrets - nothing sensitive is compiled in.
/// </summary>
internal static class AppConfig
{
    private static bool IsLanTest => 
        Environment.GetEnvironmentVariable("DEKHBHAI_LAN_TEST") == "1" ||
        Environment.GetEnvironmentVariable("DEKHBHAI_LAN_TEST")?.ToLowerInvariant() == "true";

    private static string GetLanIp()
    {
        if (!IsLanTest) return "localhost";
        
        try
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
        }
        catch { }
        return "localhost";
    }

#if DEBUG
    private const string DefaultSignalingWsUrl = "ws://localhost:8787/ws?role=host";
    private const string DefaultViewerBaseUrl = "http://localhost:8787/";
#else
    private const string DefaultSignalingWsUrl = "wss://dekh-bhai-signaling.onrender.com/ws?role=host";
    private const string DefaultViewerBaseUrl = "https://viewer-theta-ashy.vercel.app/";
#endif

    private static string GetDefaultSignalingWsUrl()
    {
        if (IsLanTest)
        {
            var lanIp = GetLanIp();
            return $"ws://{lanIp}:8787/ws?role=host";
        }
#if DEBUG
        return "ws://localhost:8787/ws?role=host";
#else
        return "wss://dekh-bhai-signaling.onrender.com/ws?role=host";
#endif
    }

    private static string GetDefaultViewerBaseUrl()
    {
        if (IsLanTest)
        {
            var lanIp = GetLanIp();
            return $"http://{lanIp}:8787/";
        }
#if DEBUG
        return "http://localhost:8787/";
#else
        return "https://viewer-theta-ashy.vercel.app/";
#endif
    }

    /// <summary>
    /// WebSocket URL (including the `role=host` query param the signaling server expects) the
    /// host connects to. Override with the DEKHBHAI_SIGNALING_WS_URL environment variable -
    /// e.g. "wss://cast.example.com/ws?role=host" for a production deployment.
    /// </summary>
    public static Uri SignalingWsUrl =>
        new(Environment.GetEnvironmentVariable("DEKHBHAI_SIGNALING_WS_URL") ?? GetDefaultSignalingWsUrl());

    /// <summary>
    /// Base HTTP(S) URL the generated viewer share link is built from
    /// (`{ViewerBaseUrl}?session={id}`). Override with DEKHBHAI_VIEWER_BASE_URL.
    /// </summary>
    public static Uri ViewerBaseUrl =>
        new(Environment.GetEnvironmentVariable("DEKHBHAI_VIEWER_BASE_URL") ?? GetDefaultViewerBaseUrl());

    /// <summary>
    /// Whether the app is running in LAN test mode.
    /// </summary>
    public static bool LanTestMode => IsLanTest;

    /// <summary>
    /// The LAN IP address to use for generating control URLs in LAN test mode.
    /// </summary>
    public static string LanIp => IsLanTest ? GetLanIp() : "localhost";
}
