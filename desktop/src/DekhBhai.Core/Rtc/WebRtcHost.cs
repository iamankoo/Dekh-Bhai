using System.Collections.Concurrent;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace DekhBhai.Core.Rtc;

/// <summary>
/// Owns one <see cref="RTCPeerConnection"/> per connected viewer and fans encoded video/audio
/// samples out to all of them. The signaling server only ever sees the SDP/ICE messages this
/// class produces - media flows directly over WebRTC to each viewer. One capture/encode
/// pipeline is shared across every viewer - see <see cref="SendVideo"/>/<see cref="SendAudio"/> -
/// the screen is never captured or encoded per-viewer.
/// Also manages a single control peer connection for remote desktop input.
/// </summary>
public sealed class WebRtcHost : IAsyncDisposable
{
    private static readonly VideoFormat VideoFormatVp8 = new(VideoCodecsEnum.VP8, 100);
    private static readonly AudioFormat AudioFormatOpus = new(AudioCodecsEnum.OPUS, 111, clockRate: 48000, channelCount: 2);

    private static readonly TimeSpan DisconnectedGracePeriod = TimeSpan.FromSeconds(12);

    public event Action<int>? ViewerCountChanged;
    public event Action<string, RTCPeerConnectionState>? ViewerConnectionStateChanged;
    public event Action<string>? Error;

    // Control events
    public event Action<string>? ControlConnected;
    public event Action<string>? ControlDisconnected;
    public event Action<ControlCommand>? ControlCommandReceived;

    private readonly SignalingClient _signaling;
    private readonly ConcurrentDictionary<string, RTCPeerConnection> _viewers = new();
    private readonly ConcurrentDictionary<string, Timer> _disconnectGraceTimers = new();
    private IReadOnlyList<RTCIceServer> _iceServers = Array.Empty<RTCIceServer>();

    // Control connection (single controller at a time)
    private RTCPeerConnection? _controlPc;
    private string? _controlSessionId;
    private RTCDataChannel? _controlDataChannel;
    private Timer? _controlDisconnectTimer;

    public WebRtcHost(SignalingClient signaling)
    {
        _signaling = signaling;
        _signaling.ViewerJoined += OnViewerJoined;
        _signaling.ViewerLeft += OnViewerLeft;
        _signaling.AnswerReceived += OnAnswerReceived;
        _signaling.IceCandidateReceived += OnIceCandidateReceived;

        // Control signaling events
        _signaling.ControlAnswerReceived += OnControlAnswerReceived;
        _signaling.ControlIceCandidateReceived += OnControlIceCandidateReceived;
        _signaling.ControlViewerJoined += OnControlViewerJoined;
        _signaling.ControlViewerLeft += OnControlViewerLeft;
    }

    public int ViewerCount => _viewers.Count;
    public bool HasControlConnection => _controlPc != null && _controlPc.connectionState == RTCPeerConnectionState.connected;

    public void SetIceServers(IReadOnlyList<IceServerPayload> iceServers)
    {
        _iceServers = iceServers
            .Select(s => new RTCIceServer
            {
                urls = s.Urls,
                username = s.Username,
                credential = s.Credential,
            })
            .ToList();
    }

    public IReadOnlyList<RTCIceServer> GetIceServers() => _iceServers;

    // ========== Viewer Management ==========

    private async void OnViewerJoined(string viewerId, int viewerCount)
    {
        try
        {
            var config = new RTCConfiguration { iceServers = _iceServers.ToList() };
            var pc = new RTCPeerConnection(config);

            pc.addTrack(new MediaStreamTrack(VideoFormatVp8, MediaStreamStatusEnum.SendOnly));
            pc.addTrack(new MediaStreamTrack(AudioFormatOpus, MediaStreamStatusEnum.SendOnly));

            pc.onicecandidate += candidate =>
            {
                if (candidate is null) return;
                _ = _signaling.SendIceCandidateAsync(viewerId, new IceCandidatePayload
                {
                    Candidate = candidate.candidate,
                    SdpMid = candidate.sdpMid,
                    SdpMLineIndex = candidate.sdpMLineIndex,
                });
            };

            pc.onconnectionstatechange += state => HandleConnectionStateChange(viewerId, state);

            _viewers[viewerId] = pc;
            ViewerCountChanged?.Invoke(ViewerCount);

            var offer = pc.createOffer(null);
            await pc.setLocalDescription(offer);
            await _signaling.SendOfferAsync(viewerId, offer.sdp);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"failed to negotiate with viewer {viewerId}: {ex.Message}");
        }
    }

    private void HandleConnectionStateChange(string viewerId, RTCPeerConnectionState state)
    {
        ViewerConnectionStateChanged?.Invoke(viewerId, state);

        switch (state)
        {
            case RTCPeerConnectionState.disconnected:
                ScheduleDisconnectGraceTimeout(viewerId);
                break;
            case RTCPeerConnectionState.connected:
                CancelDisconnectGraceTimeout(viewerId);
                break;
            case RTCPeerConnectionState.failed:
            case RTCPeerConnectionState.closed:
                CancelDisconnectGraceTimeout(viewerId);
                RemoveViewer(viewerId);
                break;
        }
    }

    private void ScheduleDisconnectGraceTimeout(string viewerId)
    {
        CancelDisconnectGraceTimeout(viewerId);
        var timer = new Timer(_ => RemoveViewer(viewerId), null, DisconnectedGracePeriod, Timeout.InfiniteTimeSpan);
        _disconnectGraceTimers[viewerId] = timer;
    }

    private void CancelDisconnectGraceTimeout(string viewerId)
    {
        if (_disconnectGraceTimers.TryRemove(viewerId, out var timer))
        {
            timer.Dispose();
        }
    }

    private void RemoveViewer(string viewerId)
    {
        if (_viewers.TryRemove(viewerId, out var pc))
        {
            pc.close();
            ViewerCountChanged?.Invoke(ViewerCount);
        }
    }

    private void OnViewerLeft(string viewerId, int viewerCount) => RemoveViewer(viewerId);

    private void OnAnswerReceived(string viewerId, string sdp)
    {
        if (_viewers.TryGetValue(viewerId, out var pc))
        {
            pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
        }
    }

    private void OnIceCandidateReceived(string viewerId, IceCandidatePayload candidate)
    {
        if (_viewers.TryGetValue(viewerId, out var pc))
        {
            pc.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid,
                sdpMLineIndex = candidate.SdpMLineIndex,
            });
        }
    }

    // ========== Control Connection Management ==========

    /// <summary>
    /// Called when control viewer joins - creates the control peer connection and sends offer.
    /// </summary>
    public async Task CreateControlConnectionAsync(string controlSessionId, IReadOnlyList<RTCIceServer> iceServers)
    {
        // Only one controller at a time - a second control session being authorized while an
        // earlier one is still connecting (or already active) must cleanly replace it, not
        // silently overwrite _controlPc/_controlDataChannel out from under an in-flight
        // negotiation. Found during testing: two overlapping control requests corrupted both
        // connections, leaving each stuck at connectionState "connecting" forever even though ICE
        // itself connected fine.
        if (_controlPc != null)
        {
            CleanupControlConnection();
        }

        _controlSessionId = controlSessionId;
        _iceServers = iceServers;

        try
        {
            var config = new RTCConfiguration { iceServers = _iceServers.ToList() };
            _controlPc = new RTCPeerConnection(config);

            // The control connection must carry the same live video/audio as a normal viewer -
            // without these tracks the phone's control page never receives a picture, so
            // videoEl.videoWidth stays 0, getNormalizedCoords() always returns null, and every
            // touch is silently dropped before a single control command is ever sent. This was
            // the actual reason remote control did nothing end-to-end despite the data channel,
            // signaling, and InputInjector all being wired correctly.
            _controlPc.addTrack(new MediaStreamTrack(VideoFormatVp8, MediaStreamStatusEnum.SendOnly));
            _controlPc.addTrack(new MediaStreamTrack(AudioFormatOpus, MediaStreamStatusEnum.SendOnly));

            // Create data channel for control commands
            _controlDataChannel = await _controlPc.createDataChannel("control", new RTCDataChannelInit { ordered = true });
            SetupControlDataChannel(_controlDataChannel);

            _controlPc.onicecandidate += candidate =>
            {
                if (candidate is null) return;
                _ = _signaling.SendControlIceCandidateAsync(_controlSessionId!, new IceCandidatePayload
                {
                    Candidate = candidate.candidate,
                    SdpMid = candidate.sdpMid,
                    SdpMLineIndex = candidate.sdpMLineIndex,
                });
            };

            _controlPc.onconnectionstatechange += state => HandleControlConnectionStateChange(state);

            var offer = _controlPc.createOffer(null);
            await _controlPc.setLocalDescription(offer);
            await _signaling.SendControlOfferAsync(_controlSessionId, offer.sdp);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"failed to create control connection: {ex.Message}");
        }
    }

    private void SetupControlDataChannel(RTCDataChannel dc)
    {
        dc.onopen += () =>
        {
            ControlConnected?.Invoke(_controlSessionId!);
            CancelControlDisconnectTimer();
        };

        dc.onclose += () =>
        {
            ControlDisconnected?.Invoke(_controlSessionId!);
            ScheduleControlDisconnectCleanup();
        };

        dc.onmessage += new SIPSorcery.Net.OnDataChannelMessageDelegate((RTCDataChannel dc, SIPSorcery.Net.DataChannelPayloadProtocols protocol, byte[] data) =>
        {
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(data);
                var command = System.Text.Json.JsonSerializer.Deserialize<ControlCommand>(json);
                if (command != null)
                {
                    ControlCommandReceived?.Invoke(command);
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke($"failed to parse control command: {ex.Message}");
            }
        });

        dc.onerror += (err) =>
        {
            Error?.Invoke($"control data channel error: {err}");
        };
    }

    

    private void HandleControlConnectionStateChange(RTCPeerConnectionState state)
    {
        switch (state)
        {
            case RTCPeerConnectionState.connected:
                CancelControlDisconnectTimer();
                break;
            case RTCPeerConnectionState.disconnected:
                ScheduleControlDisconnectTimer();
                break;
            case RTCPeerConnectionState.failed:
            case RTCPeerConnectionState.closed:
                CleanupControlConnection();
                break;
        }
    }

    private void ScheduleControlDisconnectTimer()
    {
        CancelControlDisconnectTimer();
        _controlDisconnectTimer = new Timer(_ => CleanupControlConnection(), null, DisconnectedGracePeriod, Timeout.InfiniteTimeSpan);
    }

    private void CancelControlDisconnectTimer()
    {
        if (_controlDisconnectTimer != null)
        {
            _controlDisconnectTimer.Dispose();
            _controlDisconnectTimer = null;
        }
    }

    private void ScheduleControlDisconnectCleanup()
    {
        CancelControlDisconnectTimer();
        _controlDisconnectTimer = new Timer(_ => CleanupControlConnection(), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    private void CleanupControlConnection()
    {
        CancelControlDisconnectTimer();
        if (_controlPc != null)
        {
            _controlPc.close();
            _controlPc = null;
        }
        _controlDataChannel = null;
        _controlSessionId = null;
    }

    private void OnControlAnswerReceived(string controlSessionId, string sdp)
    {
        if (_controlPc != null && _controlSessionId == controlSessionId)
        {
            _controlPc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
        }
    }

    private void OnControlIceCandidateReceived(string controlSessionId, IceCandidatePayload candidate)
    {
        if (_controlPc != null && _controlSessionId == controlSessionId)
        {
            _controlPc.addIceCandidate(new RTCIceCandidateInit
            {
                candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid,
                sdpMLineIndex = candidate.SdpMLineIndex,
            });
        }
    }

    private void OnControlViewerJoined(string controlSessionId)
    {
        // Viewer joined - connection will be established via offer/answer
    }

    private void OnControlViewerLeft(string controlSessionId)
    {
        if (_controlSessionId == controlSessionId)
        {
            CleanupControlConnection();
        }
    }

    // ========== Media Streaming ==========

    public void SendVideo(byte[] sample, uint durationRtpUnits)
    {
        foreach (var pc in _viewers.Values)
        {
            if (pc.connectionState == RTCPeerConnectionState.connected)
            {
                pc.SendVideo(durationRtpUnits, sample);
            }
        }
        if (_controlPc is { connectionState: RTCPeerConnectionState.connected })
        {
            _controlPc.SendVideo(durationRtpUnits, sample);
        }
    }

    public void SendAudio(byte[] sample, uint durationRtpUnits)
    {
        foreach (var pc in _viewers.Values)
        {
            if (pc.connectionState == RTCPeerConnectionState.connected)
            {
                pc.SendAudio(durationRtpUnits, sample);
            }
        }
        if (_controlPc is { connectionState: RTCPeerConnectionState.connected })
        {
            _controlPc.SendAudio(durationRtpUnits, sample);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _signaling.ViewerJoined -= OnViewerJoined;
        _signaling.ViewerLeft -= OnViewerLeft;
        _signaling.AnswerReceived -= OnAnswerReceived;
        _signaling.IceCandidateReceived -= OnIceCandidateReceived;
        _signaling.ControlAnswerReceived -= OnControlAnswerReceived;
        _signaling.ControlIceCandidateReceived -= OnControlIceCandidateReceived;
        _signaling.ControlViewerJoined -= OnControlViewerJoined;
        _signaling.ControlViewerLeft -= OnControlViewerLeft;

        foreach (var timer in _disconnectGraceTimers.Values) timer.Dispose();
        _disconnectGraceTimers.Clear();

        foreach (var pc in _viewers.Values)
        {
            pc.close();
        }
        _viewers.Clear();

        CleanupControlConnection();

        await Task.CompletedTask;
    }
}