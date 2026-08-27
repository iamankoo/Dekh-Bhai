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
    public event Action<string, string, IReadOnlyList<IceServerPayload>>? SessionCreated;
    public event Action<DateTimeOffset, DateTimeOffset?>? LiveAck;
    public event Action? SessionExpired;
    public event Action<string>? SessionEnded;
    public event Action<string, int>? ViewerJoined;
    public event Action<string, int>? ViewerLeft;
    public event Action<string, string>? AnswerReceived;
    public event Action<string, IceCandidatePayload>? IceCandidateReceived;
    public event Action<string>? SignalingError;
    public event Action<string>? TransportError;
    public event Action? Disconnected;
    public event Action<IReadOnlyList<IceServerPayload>>? Resumed;

    // Control session events
    public event Action<ControlSessionInfo>? ControlSessionCreated;
    public event Action<string>? ControlSessionAuthorized;
    public event Action<string>? ControlSessionRevoked;
    public event Action<List<ControlSessionInfo>>? ControlSessionsListed;
    public event Action<string, string>? ControlAnswerReceived;
    public event Action<string, IceCandidatePayload>? ControlIceCandidateReceived;
    public event Action<string>? ControlViewerJoined;
    public event Action<string>? ControlViewerLeft;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private string? _sessionId;
    private string? _hostToken;
    private Uri? _signalingUri;

    public string? SessionId => _sessionId;

    public string? LastResumeFailureReason { get; private set; }

    public async Task ConnectAsync(Uri signalingUri, CancellationToken ct = default)
    {
        _signalingUri = signalingUri;
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(signalingUri, ct);
        }
        catch (Exception ex)
        {
            TransportError?.Invoke(ex.Message);
            throw;
        }
        _socket = socket;
        _cts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, _cts.Token));
    }

    public async Task<bool> ReconnectAndResumeAsync(CancellationToken ct = default)
    {
        LastResumeFailureReason = null;
        if (_signalingUri is null || _sessionId is null || _hostToken is null) return false;

        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(_signalingUri, ct);
        }
        catch (Exception ex)
        {
            TransportError?.Invoke(ex.Message);
            return false;
        }

        _socket = socket;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, _cts.Token));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResumed(IReadOnlyList<IceServerPayload> _) => tcs.TrySetResult(true);
        void OnError(string reason)
        {
            LastResumeFailureReason = reason;
            tcs.TrySetResult(false);
        }
        Resumed += OnResumed;
        SignalingError += OnError;
        try
        {
            await SendResumeSessionAsync(ct);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8), ct);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Resumed -= OnResumed;
            SignalingError -= OnError;
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socketForThisLoop, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (socketForThisLoop.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socketForThisLoop.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(ms.ToArray());
                Dispatch(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TransportError?.Invoke(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_socket, socketForThisLoop))
            {
                Disconnected?.Invoke();
            }
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
            case "resumed" when msg.SessionId is not null:
                Resumed?.Invoke(msg.IceServers ?? new List<IceServerPayload>());
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
            case "control-session-created" when msg is { ControlSessionId: not null, PairingCode: not null, ControlToken: not null }:
                ControlSessionCreated?.Invoke(new ControlSessionInfo
                {
                    ControlSessionId = msg.ControlSessionId,
                    PairingCode = msg.PairingCode,
                    ControlToken = msg.ControlToken,
                    ExpiresAt = msg.ExpiresAt,
                });
                break;
            case "control-session-authorized" when msg.ControlSessionId is not null:
                ControlSessionAuthorized?.Invoke(msg.ControlSessionId);
                break;
            case "control-session-revoked" when msg.ControlSessionId is not null:
                ControlSessionRevoked?.Invoke(msg.ControlSessionId);
                break;
            case "control-sessions-list" when msg.ControlSessions is not null:
                ControlSessionsListed?.Invoke(msg.ControlSessions);
                break;
            case "control-answer" when msg is { ControlSessionId: not null, Sdp: not null }:
                ControlAnswerReceived?.Invoke(msg.ControlSessionId, msg.Sdp);
                break;
            case "control-ice-candidate" when msg is { ControlSessionId: not null, Candidate: not null }:
                ControlIceCandidateReceived?.Invoke(msg.ControlSessionId, msg.Candidate);
                break;
            case "control-viewer-joined" when msg.ControlSessionId is not null:
                ControlViewerJoined?.Invoke(msg.ControlSessionId);
                break;
            case "control-viewer-left" when msg.ControlSessionId is not null:
                ControlViewerLeft?.Invoke(msg.ControlSessionId);
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

    public Task SendResumeSessionAsync(CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "resume-session", SessionId = _sessionId, HostToken = _hostToken }, ct);

    public Task SendOfferAsync(string viewerId, string sdp, CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "offer", ViewerId = viewerId, Sdp = sdp, HostToken = _hostToken }, ct);

    public Task SendIceCandidateAsync(string viewerId, IceCandidatePayload candidate, CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "ice-candidate", ViewerId = viewerId, Candidate = candidate, HostToken = _hostToken }, ct);

    // Control session methods
    public Task SendCreateControlSessionAsync(CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "create-control-session", HostToken = _hostToken }, ct);

    public Task SendAuthorizeControlSessionAsync(string controlSessionId, CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "authorize-control-session", ControlSessionId = controlSessionId, HostToken = _hostToken }, ct);

    public Task SendRevokeControlSessionAsync(string controlSessionId, CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "revoke-control-session", ControlSessionId = controlSessionId, HostToken = _hostToken }, ct);

    public Task SendListControlSessionsAsync(CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "list-control-sessions", HostToken = _hostToken }, ct);

    public Task SendControlOfferAsync(string controlSessionId, string sdp, CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "control-offer", ControlSessionId = controlSessionId, Sdp = sdp, HostToken = _hostToken }, ct);

    public Task SendControlIceCandidateAsync(string controlSessionId, IceCandidatePayload candidate, CancellationToken ct = default) =>
        SendAsync(new SignalingMessage { Type = "control-ice-candidate", ControlSessionId = controlSessionId, Candidate = candidate, HostToken = _hostToken }, ct);

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
            catch { }
        }
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; } catch { }
        }
        _socket?.Dispose();
        _cts?.Dispose();
    }
}