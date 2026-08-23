using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DekhBhai.Core.Session;

namespace DekhBhai.Core.Rtc;

/// <summary>
/// Host-side WebSocket client for the Node signaling relay in signaling/src/server.js. Carries
/// only session-lifecycle/SDP/ICE JSON - never media. One instance corresponds to one session
/// (create-session -> host-live -> ... -> stop-session), matching the server's session state
/// machine in signaling/src/sessionStateMachine.js.
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    public event Action<string, string, IReadOnlyList<IceServerPayload>>? SessionCreated; // sessionId, hostToken, iceServers
    public event Action<DateTimeOffset, DateTimeOffset?>? LiveAck; // startedAt, expiresAt (null = untilStopped)
    public event Action? SessionExpired;
    public event Action<string>? SessionEnded; // reason
    public event Action<string, int>? ViewerJoined; // viewerId, viewerCount
    public event Action<string, int>? ViewerLeft; // viewerId, viewerCount
    public event Action<string, string>? AnswerReceived; // viewerId, sdp
    public event Action<string, IceCandidatePayload>? IceCandidateReceived; // viewerId, candidate
    public event Action<string>? SignalingError; // reason - a recognized protocol-level error from the server
    public event Action<string>? TransportError; // a connection-level failure (network, refused, etc.)
    public event Action? Disconnected;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private string? _sessionId;
    private string? _hostToken;

    public string? SessionId => _sessionId;

    public async Task ConnectAsync(Uri signalingUri, CancellationToken ct = default)
    {
        _socket = new ClientWebSocket();
        _cts = new CancellationTokenSource();
        try
        {
            await _socket.ConnectAsync(signalingUri, ct);
        }
        catch (Exception ex)
        {
            TransportError?.Invoke(ex.Message);
            throw;
        }
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (_socket is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(ms.ToArray());
                Dispatch(text);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            TransportError?.Invoke(ex.Message);
        }
        finally
        {
            Disconnected?.Invoke();
        }
    }

    private void Dispatch(string json)
    {
        SignalingMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<SignalingMessage>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            SignalingError?.Invoke($"malformed signaling message: {ex.Message}");
            return;
        }
        if (msg is null) return;

        switch (msg.Type)
        {
            case "session-created" when msg is { SessionId: not null, HostToken: not null }:
                _sessionId = msg.SessionId;
                _hostToken = msg.HostToken;
                SessionCreated?.Invoke(msg.SessionId, msg.HostToken, msg.IceServers ?? new List<IceServerPayload>());
                break;
            case "live-ack" when msg.StartedAt is not null:
                var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(msg.StartedAt.Value);
                var expiresAt = msg.ExpiresAt is { } exp ? DateTimeOffset.FromUnixTimeMilliseconds(exp) : (DateTimeOffset?)null;
                LiveAck?.Invoke(startedAt, expiresAt);
                break;
            case "session-expired":
                SessionExpired?.Invoke();
                break;
            case "session-ended":
                SessionEnded?.Invoke(msg.Reason ?? "unknown");
                break;
            case "viewer-joined" when msg.ViewerId is not null:
                ViewerJoined?.Invoke(msg.ViewerId, msg.ViewerCount ?? 0);
                break;
            case "viewer-left" when msg.ViewerId is not null:
                ViewerLeft?.Invoke(msg.ViewerId, msg.ViewerCount ?? 0);
                break;
            case "answer" when msg is { ViewerId: not null, Sdp: not null }:
                AnswerReceived?.Invoke(msg.ViewerId, msg.Sdp);
                break;
            case "ice-candidate" when msg is { ViewerId: not null, Candidate: not null }:
                IceCandidateReceived?.Invoke(msg.ViewerId, msg.Candidate);
                break;
            case "error":
                SignalingError?.Invoke(msg.Reason ?? "unknown signaling error");
                break;
        }
    }

    public Task SendCreateSessionAsync(SessionDuration duration, CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "create-session", Duration = duration.WireId() }, ct);

    public Task SendHostLiveAsync(CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "host-live", HostToken = _hostToken }, ct);

    public Task SendHeartbeatAsync(CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "heartbeat", HostToken = _hostToken }, ct);

    public Task SendStopSessionAsync(CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "stop-session", HostToken = _hostToken }, ct);

    public Task SendOfferAsync(string viewerId, string sdp, CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "offer", ViewerId = viewerId, Sdp = sdp, HostToken = _hostToken }, ct);

    public Task SendIceCandidateAsync(string viewerId, IceCandidatePayload candidate, CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "ice-candidate", ViewerId = viewerId, Candidate = candidate, HostToken = _hostToken }, ct);

    private async Task SendAsync(SignalingMessage message, CancellationToken ct)
    {
        if (_socket is not { State: WebSocketState.Open }) return;
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (Exception ex)
        {
            TransportError?.Invoke(ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_socket is { State: WebSocketState.Open })
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "host closed session", CancellationToken.None);
            }
            catch { /* best-effort */ }
        }
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; } catch { /* already handled/logged */ }
        }
        _socket?.Dispose();
        _cts?.Dispose();
    }
}
