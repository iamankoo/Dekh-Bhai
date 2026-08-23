using System.Text.Json;
using DekhBhai.Core.Rtc;

namespace DekhBhai.Core.Tests;

/// <summary>
/// The wire format here must stay in sync with signaling/src/server.js and viewer/viewer.js,
/// which speak plain camelCase JSON with the same field names. These tests exist to catch an
/// accidental rename/casing change on the C# side before it breaks the Node relay silently.
/// </summary>
public class SignalingMessageTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void OfferMessage_SerializesWithExpectedFieldNames()
    {
        var msg = new SignalingMessage { Type = "offer", ViewerId = "abc123", Sdp = "v=0..." };

        var json = JsonSerializer.Serialize(msg, Options);

        Assert.Contains("\"type\":\"offer\"", json);
        Assert.Contains("\"viewerId\":\"abc123\"", json);
        Assert.Contains("\"sdp\":\"v=0...\"", json);
    }

    [Fact]
    public void IceCandidateMessage_RoundTripsThroughJson()
    {
        var original = new SignalingMessage
        {
            Type = "ice-candidate",
            ViewerId = "viewer-1",
            Candidate = new IceCandidatePayload
            {
                Candidate = "candidate:1 1 udp 2122260223 192.168.1.5 54321 typ host",
                SdpMid = "0",
                SdpMLineIndex = 0,
            },
        };

        var json = JsonSerializer.Serialize(original, Options);
        var roundTripped = JsonSerializer.Deserialize<SignalingMessage>(json, Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Type, roundTripped!.Type);
        Assert.Equal(original.ViewerId, roundTripped.ViewerId);
        Assert.Equal(original.Candidate!.Candidate, roundTripped.Candidate!.Candidate);
        Assert.Equal(original.Candidate.SdpMid, roundTripped.Candidate.SdpMid);
    }

    [Fact]
    public void SessionCreatedMessage_FromServerJson_Deserializes()
    {
        // Exact shape the Node signaling server sends (see signaling/src/server.js send()).
        const string serverJson = """{"type":"session-created","sessionId":"AbCdEf12"}""";

        var msg = JsonSerializer.Deserialize<SignalingMessage>(serverJson, Options);

        Assert.NotNull(msg);
        Assert.Equal("session-created", msg!.Type);
        Assert.Equal("AbCdEf12", msg.SessionId);
    }
}
