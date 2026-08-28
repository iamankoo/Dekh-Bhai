using DekhBhai.Core.Audio;
using DekhBhai.Core.Capture;
using DekhBhai.Core.Input;
using DekhBhai.Core.Media;
using DekhBhai.Core.Rtc;

namespace DekhBhai.Core.Session;

/// <summary>
/// Ties the capture, audio, media, and WebRTC layers together behind Start/Stop. This is the
/// "Session Manager" in the architecture diagram - the desktop UI talks only to this class and
/// never touches capture/audio/encoder/WebRTC objects directly, so the UI can be minimized,
/// swapped, or extended without changing the media engine.
///
/// Session lifecycle mirrors the server's own state machine (signaling/src/sessionStateMachine.js):
/// create-session -> (capture/audio start locally) -> host-live -> heartbeat while Live ->
/// stop-session | session-expired -> full local teardown. The server is authoritative for
/// expiration and for whether the session still exists at all - this class never assumes a
/// fixed-duration session is still valid just because its own local timer hasn't fired.
/// </summary>
public sealed class SessionController : IAsyncDisposable
{
    public event Action<SessionState, SessionStopReason?>? StateChanged;
    public event Action<int>? ViewerCountChanged;
    public event Action<string>? ShareUrlReady;
    public event Action<DateTimeOffset, DateTimeOffset?>? SessionTimingReady;
    public event Action<string>? CaptureStatusChanged;
    public event Action<string>? AudioStatusChanged;
    public event Action<string>? SignalingStatusChanged;

    // Control session events - a viewer on the normal share link clicked "Remote Control" and is
    // waiting for this host to Allow/Deny (see ControlRequestReceived), or the resulting session's
    // subsequent lifecycle once allowed.
    public event Action<string, string>? ControlRequestReceived; // controlSessionId, viewerId
    public event Action<string>? ControlSessionAuthorized;
    public event Action<string>? ControlSessionRevoked;
    public event Action<string>? ControlSessionConnected;
    public event Action<string>? ControlSessionDisconnected;
    public event Action<List<ControlSessionInfo>>? ControlSessionsListed;

    public SessionState State { get; private set; } = SessionState.Idle;

    private readonly Uri _signalingWsUri;
    private readonly Uri _viewerBaseHttpUri;
    private readonly string _lanIp;

    private IScreenCapture? _capture;
    private IAudioCapture? _systemAudio;
    private VideoEncoderPipeline? _videoPipeline;
    private AudioEncoderPipeline? _audioPipeline;
    private SignalingClient? _signaling;
    private WebRtcHost? _webRtc;
    private InputInjector? _inputInjector;
    private Timer? _heartbeatTimer;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private bool _reconnecting;

    private static readonly int[] ReconnectDelaysSeconds = { 2, 3, 4, 5, 5 };
    private static readonly int[] InitialConnectDelaysSeconds = { 2, 3, 5, 8, 10 };

    public SessionController(Uri signalingWsUri, Uri viewerBaseHttpUri, string lanIp = "localhost")
    {
        _signalingWsUri = signalingWsUri;
        _viewerBaseHttpUri = viewerBaseHttpUri;
        _lanIp = lanIp;
    }

    public async Task StartAsync(CaptureSettings captureSettings, SessionDuration duration)
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (State is SessionState.Live or SessionState.Starting) return;
            SetState(SessionState.Starting);

            try
            {
                FFmpegBootstrap.EnsureInitialised();

                _capture = new WindowsGraphicsScreenCapture();
                _systemAudio = new WasapiLoopbackAudioCapture();
                _videoPipeline = new VideoEncoderPipeline(_capture, captureSettings.TargetFramesPerSecond);
                _audioPipeline = new AudioEncoderPipeline(_systemAudio);

                var (sessionId, iceServers) = await EstablishSignalingAsync(duration);

                _signaling!.SignalingError += msg => CaptureStatusChanged?.Invoke(TranslateError(msg));
                _signaling.TransportError += msg => CaptureStatusChanged?.Invoke(TranslateError(msg));

                _webRtc = new WebRtcHost(_signaling);
                _webRtc.SetIceServers(iceServers);
                _webRtc.ViewerCountChanged += count => ViewerCountChanged?.Invoke(count);
                _webRtc.Error += msg => CaptureStatusChanged?.Invoke(msg);

                // Control session events
                _webRtc.ControlConnected += OnControlConnected;
                _webRtc.ControlDisconnected += OnControlDisconnected;
                _webRtc.ControlCommandReceived += OnControlCommandReceived;

                // Input injector for remote control
                _inputInjector = new InputInjector(captureSettings.TargetWidth, captureSettings.TargetHeight);

                _videoPipeline.EncodedSampleReady += (sample, dur) => _webRtc.SendVideo(sample, dur);
                _videoPipeline.CaptureStateChanged += (_, evt) => ReportCaptureEvent(evt);

                _audioPipeline.EncodedSampleReady += (sample, dur) => _webRtc.SendAudio(sample, dur);
                _audioPipeline.EncodeError += (_, msg) => AudioStatusChanged?.Invoke(msg);

                _systemAudio.CaptureError += (_, msg) => AudioStatusChanged?.Invoke(msg);

                var liveAck = new TaskCompletionSource<(DateTimeOffset startedAt, DateTimeOffset? expiresAt)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _signaling.LiveAck += (startedAt, expiresAt) => liveAck.TrySetResult((startedAt, expiresAt));
                _signaling.SessionExpired += () => _ = StopAsync(SessionStopReason.DurationExpired);
                _signaling.SessionEnded += reason => _ = StopAsync(reason == "host-timeout"
                    ? SessionStopReason.FatalError
                    : SessionStopReason.UserRequested);
                _signaling.Disconnected += OnSignalingDisconnected;
                _signaling.Resumed += iceServers =>
                {
                    _webRtc?.SetIceServers(iceServers);
                    SignalingStatusChanged?.Invoke("connected");
                };

                // Control session signaling events
                _signaling.ControlRequestReceived += OnControlRequestReceived;
                _signaling.ControlSessionAuthorized += OnControlSessionAuthorized;
                _signaling.ControlSessionRevoked += OnControlSessionRevoked;
                _signaling.ControlSessionsListed += OnControlSessionsListed;
                _signaling.ControlViewerJoined += OnControlViewerJoined;
                _signaling.ControlViewerLeft += OnControlViewerLeft;

                _capture.Start(captureSettings);
                _systemAudio.Start();

                await _signaling.SendHostLiveAsync();
                var (startedAt, expiresAt) = await liveAck.Task.WaitAsync(TimeSpan.FromSeconds(10));

                var shareUrl = new Uri(_viewerBaseHttpUri, $"v/{sessionId}").ToString();
                ShareUrlReady?.Invoke(shareUrl);
                SessionTimingReady?.Invoke(startedAt, expiresAt);
                CaptureStatusChanged?.Invoke("capturing");
                AudioStatusChanged?.Invoke("capturing system audio");
                SignalingStatusChanged?.Invoke("connected");

                _heartbeatTimer = new Timer(
                    _ => _ = _signaling?.SendHeartbeatAsync(),
                    null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

                SetState(SessionState.Live);
            }
            catch (Exception ex)
            {
                CaptureStatusChanged?.Invoke(TranslateError(ex.Message));
                await TearDownAsync();
                SetState(SessionState.Error, SessionStopReason.FatalError);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<(string sessionId, IReadOnlyList<IceServerPayload> iceServers)> EstablishSignalingAsync(SessionDuration duration)
    {
        Exception? lastError = null;

        for (int attempt = 1; attempt <= InitialConnectDelaysSeconds.Length + 1; attempt++)
        {
            var candidate = new SignalingClient();
            try
            {
                var created = new TaskCompletionSource<(string sessionId, IReadOnlyList<IceServerPayload> ice)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                candidate.SessionCreated += (id, _, ice) => created.TrySetResult((id, ice));

                CaptureStatusChanged?.Invoke(attempt == 1 ? "Connecting to Dekh Bhai..." : "Still connecting to Dekh Bhai...");

                await candidate.ConnectAsync(_signalingWsUri);
                await candidate.SendCreateSessionAsync(duration);
                var (sessionId, iceServers) = await created.Task.WaitAsync(TimeSpan.FromSeconds(8));

                _signaling = candidate;
                return (sessionId, iceServers);
            }
            catch (Exception ex)
            {
                lastError = ex;
                await candidate.DisposeAsync();
            }

            if (attempt <= InitialConnectDelaysSeconds.Length)
            {
                await Task.Delay(TimeSpan.FromSeconds(InitialConnectDelaysSeconds[attempt - 1]));
            }
        }

        throw lastError ?? new InvalidOperationException("failed to establish signaling connection");
    }

    // Control session public methods

    public async Task AuthorizeControlSessionAsync(string controlSessionId)
    {
        if (_signaling == null) return;
        await _signaling.SendAuthorizeControlSessionAsync(controlSessionId);
    }

    public async Task DenyControlSessionAsync(string controlSessionId)
    {
        if (_signaling == null) return;
        await _signaling.SendDenyControlSessionAsync(controlSessionId);
    }

    public async Task RevokeControlSessionAsync(string controlSessionId)
    {
        if (_signaling == null) return;
        await _signaling.SendRevokeControlSessionAsync(controlSessionId);
    }

    public async Task ListControlSessionsAsync()
    {
        if (_signaling == null) return;
        await _signaling.SendListControlSessionsAsync();
    }

    // Control session signaling event handlers

    private void OnControlRequestReceived(string controlSessionId, string viewerId)
    {
        ControlRequestReceived?.Invoke(controlSessionId, viewerId);
    }

    private void OnControlSessionAuthorized(string controlSessionId)
    {
        ControlSessionAuthorized?.Invoke(controlSessionId);
        CaptureStatusChanged?.Invoke("Control session authorized");
    }

    private void OnControlSessionRevoked(string controlSessionId)
    {
        ControlSessionRevoked?.Invoke(controlSessionId);
        CaptureStatusChanged?.Invoke("Control session revoked");
    }

    private void OnControlSessionsListed(List<ControlSessionInfo> sessions)
    {
        ControlSessionsListed?.Invoke(sessions);
    }

    private async void OnControlViewerJoined(string controlSessionId)
    {
        if (_webRtc != null && _signaling != null)
        {
            // Get ICE servers for control connection (reuse existing)
            await _webRtc.CreateControlConnectionAsync(controlSessionId, _webRtc.GetIceServers());
        }
    }

    private void OnControlViewerLeft(string controlSessionId)
    {
        ControlSessionDisconnected?.Invoke(controlSessionId);
        _inputInjector?.ReleaseAllKeys();
    }

    // WebRtcHost control events

    private void OnControlConnected(string controlSessionId)
    {
        ControlSessionConnected?.Invoke(controlSessionId);
        CaptureStatusChanged?.Invoke("Remote control connected");
    }

    private void OnControlDisconnected(string controlSessionId)
    {
        ControlSessionDisconnected?.Invoke(controlSessionId);
        CaptureStatusChanged?.Invoke("Remote control disconnected");
        _inputInjector?.ReleaseAllKeys();
    }

    private void OnControlCommandReceived(ControlCommand cmd)
    {
        if (_inputInjector == null) return;

        try
        {
            switch (cmd.Type)
            {
                case "mouse_move":
                    _inputInjector.InjectMouseMove(cmd.X, cmd.Y);
                    break;
                case "mouse_down":
                    _inputInjector.InjectMouseDown(cmd.Button ?? "left");
                    break;
                case "mouse_up":
                    _inputInjector.InjectMouseUp(cmd.Button ?? "left");
                    break;
                case "mouse_click":
                    _inputInjector.InjectMouseClick(cmd.Button ?? "left");
                    break;
                case "mouse_double_click":
                    _inputInjector.InjectMouseDoubleClick();
                    break;
                case "mouse_scroll":
                    _inputInjector.InjectScroll(cmd.DeltaY);
                    break;
                case "keyboard_down":
                    _inputInjector.InjectKeyDown(cmd.Key ?? "", cmd.Modifiers);
                    break;
                case "keyboard_up":
                    _inputInjector.InjectKeyUp(cmd.Key ?? "", cmd.Modifiers);
                    break;
                case "text_input":
                    _inputInjector.InjectText(cmd.Text ?? "");
                    break;
            }
        }
        catch (Exception ex)
        {
            CaptureStatusChanged?.Invoke($"Control command error: {ex.Message}");
        }
    }

    public async Task StopAsync(SessionStopReason reason = SessionStopReason.UserRequested)
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (State is not (SessionState.Live or SessionState.Starting)) return;
            SetState(SessionState.Stopping);

            if (reason == SessionStopReason.UserRequested && _signaling is not null)
            {
                try { await _signaling.SendStopSessionAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { }
            }

            bool confirmed = await TearDownAsync();
            SetState(confirmed ? SessionState.Stopped : SessionState.Error, reason);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<bool> TearDownAsync()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        _capture?.Stop();
        _systemAudio?.Stop();

        _videoPipeline?.Dispose();
        _audioPipeline?.Dispose();

        if (_webRtc is not null) await _webRtc.DisposeAsync();
        if (_signaling is not null) await _signaling.DisposeAsync();

        _inputInjector?.Dispose();

        bool confirmed =
            (_capture is null || !_capture.IsCapturing) &&
            (_systemAudio is null || !_systemAudio.IsCapturing) &&
            (_webRtc is null || _webRtc.ViewerCount == 0);

        _capture?.Dispose();
        _systemAudio?.Dispose();

        _capture = null;
        _systemAudio = null;
        _videoPipeline = null;
        _audioPipeline = null;
        _webRtc = null;
        _signaling = null;
        _inputInjector = null;

        return confirmed;
    }

    private void OnSignalingDisconnected()
    {
        if (State != SessionState.Live || _reconnecting) return;
        _reconnecting = true;
        _ = AttemptReconnectAsync();
    }

    private async Task AttemptReconnectAsync()
    {
        try
        {
            SignalingStatusChanged?.Invoke("disconnected - reconnecting...");
            foreach (var delaySeconds in ReconnectDelaysSeconds)
            {
                if (State != SessionState.Live || _signaling is null) return;
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                if (State != SessionState.Live || _signaling is null) return;

                bool resumed = await _signaling.ReconnectAndResumeAsync();
                if (resumed) return;

                if (_signaling.LastResumeFailureReason is "session-ended" or "unauthorized")
                {
                    break;
                }
                SignalingStatusChanged?.Invoke("reconnect failed, retrying...");
            }

            SignalingStatusChanged?.Invoke("connection lost");
            await StopAsync(SessionStopReason.FatalError);
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private void ReportCaptureEvent(CaptureEvent evt)
    {
        var text = evt.State switch
        {
            FrameState.TemporarilyUnavailable => "capture temporarily unavailable",
            FrameState.CaptureError => $"capture error: {evt.Message} (continuing)",
            FrameState.UserStopped => "capture stopped",
            _ => null,
        };
        if (text is not null) CaptureStatusChanged?.Invoke(text);
    }

    private static string TranslateError(string raw)
    {
        var lower = raw.ToLowerInvariant();
        if (lower.Contains("unable to connect") || lower.Contains("refused") || lower.Contains("no such host") || lower.Contains("timed out"))
        {
            return "Unable to connect to Dekh Bhai's signaling service. Check your Internet connection and try again.";
        }
        return $"failed to start: {raw}";
    }

    private void SetState(SessionState state, SessionStopReason? reason = null)
    {
        State = state;
        StateChanged?.Invoke(state, reason);
    }

    public async ValueTask DisposeAsync()
    {
        await TearDownAsync();
    }
}