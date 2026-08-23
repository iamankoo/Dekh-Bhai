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
/// </summary>
public sealed class WebRtcHost : IAsyncDisposable
{
    private static readonly VideoFormat VideoFormatVp8 = new(VideoCodecsEnum.VP8, 100);
    private static readonly AudioFormat AudioFormatOpus = new(AudioCodecsEnum.OPUS, 111, clockRate: 48000, channelCount: 2);

    /// <summary>
    /// A disconnected (but not yet failed/closed) peer is kept around for this long before being
    /// treated as truly gone - WebRTC connections routinely flicker through "disconnected" during
    /// a brief network hiccup, and dropping the viewer immediately would defeat the point of
    /// tolerating short outages (see the Phase 2 brief's "network recovery" requirement).
    /// </summary>
    private static readonly TimeSpan DisconnectedGracePeriod = TimeSpan.FromSeconds(12);

    public event Action<int>? ViewerCountChanged;
    public event Action<string, RTCPeerConnectionState>? ViewerConnectionStateChanged;
    public event Action<string>? Error;

    private readonly SignalingClient _signaling;
    private readonly ConcurrentDictionary<string, RTCPeerConnection> _viewers = new();
    private readonly ConcurrentDictionary<string, Timer> _disconnectGraceTimers = new();
    private IReadOnlyList<RTCIceServer> _iceServers = Array.Empty<RTCIceServer>();

    public WebRtcHost(SignalingClient signaling)
    {
        _signaling = signaling;
        _signaling.ViewerJoined += OnViewerJoined;
        _signaling.ViewerLeft += OnViewerLeft;
        _signaling.AnswerReceived += OnAnswerReceived;
        _signaling.IceCandidateReceived += OnIceCandidateReceived;
    }

    public int ViewerCount => _viewers.Count;

    /// <summary>Must be called (with the session's server-issued ICE servers) before any viewer joins.</summary>
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
                // Don't tear down immediately - give a brief network hiccup a chance to recover.
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

    public void SendVideo(byte[] sample, uint durationRtpUnits)
    {
        foreach (var pc in _viewers.Values)
        {
            if (pc.connectionState == RTCPeerConnectionState.connected)
            {
                pc.SendVideo(durationRtpUnits, sample);
            }
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
    }

    public async ValueTask DisposeAsync()
    {
        _signaling.ViewerJoined -= OnViewerJoined;
        _signaling.ViewerLeft -= OnViewerLeft;
        _signaling.AnswerReceived -= OnAnswerReceived;
        _signaling.IceCandidateReceived -= OnIceCandidateReceived;

        foreach (var timer in _disconnectGraceTimers.Values) timer.Dispose();
        _disconnectGraceTimers.Clear();

        foreach (var pc in _viewers.Values)
        {
            pc.close();
        }
        _viewers.Clear();
        await Task.CompletedTask;
    }
}
