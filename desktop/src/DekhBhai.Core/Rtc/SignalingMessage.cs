using System.Text.Json.Serialization;

namespace DekhBhai.Core.Rtc;

/// <summary>
/// Wire shape shared with signaling/src/server.js and viewer/viewer.js. Kept as one loosely
/// typed envelope (rather than a discriminated hierarchy) since the signaling server itself
/// only ever inspects "type" and a handful of routing fields and passes the rest through
/// verbatim. See signaling/src/validate.js for the authoritative per-type required-field list.
/// </summary>
public sealed class SignalingMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("viewerId")]
    public string? ViewerId { get; set; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("hostToken")]
    public string? HostToken { get; set; }

    [JsonPropertyName("duration")]
    public string? Duration { get; set; }

    [JsonPropertyName("sdp")]
    public string? Sdp { get; set; }

    [JsonPropertyName("candidate")]
    public IceCandidatePayload? Candidate { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("iceServers")]
    public List<IceServerPayload>? IceServers { get; set; }

    /// <summary>Epoch milliseconds - always server time, never the client's own clock.</summary>
    [JsonPropertyName("startedAt")]
    public long? StartedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public long? ExpiresAt { get; set; }

    [JsonPropertyName("serverNow")]
    public long? ServerNow { get; set; }

    [JsonPropertyName("viewerCount")]
    public int? ViewerCount { get; set; }

    [JsonPropertyName("controlSessions")]
    public List<ControlSessionInfo>? ControlSessions { get; set; }

    // Control session fields
    [JsonPropertyName("controlSessionId")]
    public string? ControlSessionId { get; set; }

    [JsonPropertyName("pairingCode")]
    public string? PairingCode { get; set; }

    [JsonPropertyName("controlToken")]
    public string? ControlToken { get; set; }
}

public sealed class IceCandidatePayload
{
    [JsonPropertyName("candidate")]
    public string Candidate { get; set; } = "";

    [JsonPropertyName("sdpMid")]
    public string? SdpMid { get; set; }

    [JsonPropertyName("sdpMLineIndex")]
    public ushort SdpMLineIndex { get; set; }
}

/// <summary>Mirrors the browser's RTCIceServer shape so it can be forwarded to it unchanged.</summary>
public sealed class IceServerPayload
{
    [JsonPropertyName("urls")]
    public string Urls { get; set; } = "";

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("credential")]
    public string? Credential { get; set; }
}

/// <summary>Control session info from signaling server.</summary>
public sealed class ControlSessionInfo
{
    [JsonPropertyName("controlSessionId")]
    public string ControlSessionId { get; set; } = "";

    [JsonPropertyName("pairingCode")]
    public string PairingCode { get; set; } = "";

    [JsonPropertyName("controlToken")]
    public string ControlToken { get; set; } = "";

    [JsonPropertyName("expiresAt")]
    public long? ExpiresAt { get; set; }
}