using DekhBhai.Core.Audio;
using DekhBhai.Core.Capture;
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
/// create-session -&gt; (capture/audio start locally) -&gt; host-live -&gt; heartbeat while Live -&gt;
/// stop-session | session-expired -&gt; full local teardown. The server is authoritative for
/// expiration and for whether the session still exists at all - this class never assumes a
/// fixed-duration session is still valid just because its own local timer hasn't fired.
/// </summary>
public sealed class SessionController : IAsyncDisposable
{
    public event Action<SessionState, SessionStopReason?>? StateChanged;
    public event Action<int>? ViewerCountChanged;
    public event Action<string>? ShareUrlReady;
    public event Action<DateTimeOffset, DateTimeOffset?>? SessionTimingReady; // startedAt, expiresAt
    public event Action<string>? CaptureStatusChanged;
    public event Action<string>? AudioStatusChanged;

    public SessionState State { get; private set; } = SessionState.Idle;

    private readonly Uri _signalingWsUri;
    private readonly Uri _viewerBaseHttpUri;

    private IScreenCapture? _capture;
    private IAudioCapture? _systemAudio;
    private VideoEncoderPipeline? _videoPipeline;
    private AudioEncoderPipeline? _audioPipeline;
    private SignalingClient? _signaling;
    private WebRtcHost? _webRtc;
    private Timer? _heartbeatTimer;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    public SessionController(Uri signalingWsUri, Uri viewerBaseHttpUri)
    {
        _signalingWsUri = signalingWsUri;
        _viewerBaseHttpUri = viewerBaseHttpUri;
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
                _signaling = new SignalingClient();

                var created = new TaskCompletionSource<(string sessionId, IReadOnlyList<IceServerPayload> ice)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _signaling.SessionCreated += (id, _, ice) => created.TrySetResult((id, ice));
                _signaling.SignalingError += msg => CaptureStatusChanged?.Invoke(TranslateError(msg));
                _signaling.TransportError += msg => CaptureStatusChanged?.Invoke(TranslateError(msg));

                await _signaling.ConnectAsync(_signalingWsUri);
                await _signaling.SendCreateSessionAsync(duration);
                var (sessionId, iceServers) = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));

                _webRtc = new WebRtcHost(_signaling);
                _webRtc.SetIceServers(iceServers);
                _webRtc.ViewerCountChanged += count => ViewerCountChanged?.Invoke(count);
                _webRtc.Error += msg => CaptureStatusChanged?.Invoke(msg);

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

                _capture.Start(captureSettings);
                _systemAudio.Start();

                await _signaling.SendHostLiveAsync();
                var (startedAt, expiresAt) = await liveAck.Task.WaitAsync(TimeSpan.FromSeconds(10));

                var shareUrl = new Uri(_viewerBaseHttpUri, $"v/{sessionId}").ToString();
                ShareUrlReady?.Invoke(shareUrl);
                SessionTimingReady?.Invoke(startedAt, expiresAt);
                CaptureStatusChanged?.Invoke("capturing");
                AudioStatusChanged?.Invoke("capturing system audio");

                _heartbeatTimer = new Timer(
                    _ => _ = _signaling?.SendHeartbeatAsync(),
                    null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

                SetState(SessionState.Live);
            }
            catch (Exception ex)
            {
                CaptureStatusChanged?.Invoke(TranslateError(ex.Message));
                await TearDownAsync();
                SetState(SessionState.Idle);
            }
        }
        finally
        {
            _lifecycleLock.Release();
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
                catch { /* best-effort - local teardown proceeds regardless */ }
            }

            bool confirmed = await TearDownAsync();

            // Post-session screen must only appear once every component has confirmed
            // it actually stopped - never merely because Stop was requested.
            SetState(confirmed ? SessionState.Stopped : SessionState.Error, reason);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Stops and releases every engine component. Returns true if shutdown is confirmed clean.</summary>
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

        return confirmed;
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

    /// <summary>
    /// Translates low-level exception/transport text into something a normal user can act on.
    /// Detailed diagnostics stay in the raw message passed to CaptureStatusChanged for the
    /// diagnostics panel - this only governs the headline text.
    /// </summary>
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
