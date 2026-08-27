'use strict';

/**
 * Dekh Bhai viewer.
 *
 * Minimal, dependency-free WebRTC receiver:
 *   1. Opens a WebSocket to the signaling server for the session id in the URL.
 *   2. Waits for an SDP offer from the host, answers it.
 *   3. Relays ICE candidates both ways, using the STUN/TURN servers the signaling server issued.
 *   4. Renders the received video/audio tracks and a server-time-based elapsed timer.
 *   5. On an unexpected disconnect (not an explicit session-ended/expired/unavailable), retries
 *      the whole connection with backoff - a page refresh isn't required to recover from a
 *      short network blip.
 *
 * The signaling server only ever sees SDP/ICE JSON - never media.
 */

const RECONNECT_DELAYS_MS = [1000, 2000, 4000, 8000, 15000];

const statusEl = document.getElementById('status');
const elapsedEl = document.getElementById('elapsed');
const placeholderEl = document.getElementById('placeholder');
const placeholderText = document.getElementById('placeholderText');
const videoEl = document.getElementById('remoteVideo');
const fullscreenBtn = document.getElementById('fullscreenBtn');
const statsLine = document.getElementById('statsLine');
const unmuteBtn = document.getElementById('unmuteBtn');

let pc = null;
let ws = null;
let statsTimer = null;
let elapsedTimer = null;
let reconnectAttempt = 0;
let reconnectTimer = null;
let terminal = false; // true once the session has definitively ended/expired/is unavailable
let serverClockOffsetMs = 0; // (server time) - (our Date.now()) at the moment we joined
let sessionStartedAt = null;
let sessionExpiresAt = null;

const VIEWER_STATE = {
  CONNECTING: { badge: 'Connecting…', kind: 'connecting', placeholder: 'Connecting to Dekh Bhai…' },
  LIVE: { badge: 'Live', kind: 'connected', placeholder: null },
  RECONNECTING: { badge: 'Reconnecting…', kind: 'reconnecting', placeholder: 'Reconnecting…' },
  SESSION_ENDED: { badge: 'Ended', kind: 'disconnected', placeholder: 'Sharing has ended.' },
  SESSION_EXPIRED: { badge: 'Expired', kind: 'disconnected', placeholder: 'This session has expired.' },
  SESSION_UNAVAILABLE: { badge: 'Unavailable', kind: 'disconnected', placeholder: 'This session is unavailable.' },
};

function setViewerState(name) {
  const s = VIEWER_STATE[name];
  statusEl.textContent = s.badge;
  statusEl.className = `status status--${s.kind}`;
  if (s.placeholder) {
    placeholderText.textContent = s.placeholder;
    placeholderEl.style.display = 'flex';
  } else {
    placeholderEl.style.display = 'none';
  }
}

function getSessionId() {
  // Primary: path-based /v/<id> (production URL shape). Falls back to ?session=<id> for
  // any link generated before this shape existed.
  const pathMatch = window.location.pathname.match(/\/v\/([^/]+)/);
  if (pathMatch) return decodeURIComponent(pathMatch[1]);
  return new URLSearchParams(window.location.search).get('session');
}

/**
 * The signaling origin defaults to this page's own origin (correct when signaling itself serves
 * this page, as in local development). When the viewer is deployed separately from signaling
 * (e.g. this page on Vercel, signaling on its own long-lived host), config.js sets
 * window.DEKHBHAI_SIGNALING_ORIGIN to signaling's public origin - see config.js and
 * docs/deployment/phase-2.md.
 */
function signalingOrigin() {
  const configured = window.DEKHBHAI_SIGNALING_ORIGIN;
  if (!configured) {
    const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${proto}//${window.location.host}`;
  }
  // Accept either an http(s) or already-ws(s) origin so config.js can be written either way.
  return configured.replace(/^http:/, 'ws:').replace(/^https:/, 'wss:').replace(/\/$/, '');
}

function wsUrl(sessionId) {
  return `${signalingOrigin()}/ws?role=viewer&session=${encodeURIComponent(sessionId)}`;
}

function createPeerConnection(iceServers) {
  const conn = new RTCPeerConnection({ iceServers: iceServers && iceServers.length ? iceServers : [] });

  conn.ontrack = (event) => {
    if (videoEl.srcObject !== event.streams[0]) {
      videoEl.srcObject = event.streams[0];
      fullscreenBtn.disabled = false;
      startPlayback();
    }
  };

  conn.onicecandidate = (event) => {
    if (event.candidate) send({ type: 'ice-candidate', candidate: event.candidate });
  };

  conn.onconnectionstatechange = () => {
    if (terminal) return;
    switch (conn.connectionState) {
      case 'connected':
        setViewerState('LIVE');
        reconnectAttempt = 0;
        startStatsLoop();
        break;
      case 'disconnected':
        // Transient - WebRTC flickers through this during brief network hiccups. Only escalate
        // to a full reconnect if it doesn't recover on its own shortly (handled by 'failed').
        break;
      case 'failed':
      case 'closed':
        setViewerState('RECONNECTING');
        stopStatsLoop();
        scheduleReconnect();
        break;
    }
  };

  return conn;
}

/**
 * Chrome (and most browsers) block autoplay WITH SOUND for a <video> element unless the page
 * already has enough "media engagement" or the user has interacted with it - found during
 * production testing to be a real, silent failure mode: play() rejects, the element stays
 * paused, and NOTHING is rendered or audible, while RTCPeerConnection.getStats() keeps showing
 * framesDecoded/totalSamplesReceived increasing regardless, because the underlying WebRTC decode
 * pipeline runs independently of whether the <video> element is actually playing. getStats()
 * alone is therefore not sufficient evidence that video/audio actually reached the screen or
 * speakers - see docs/architecture/phase-3-technology-decision.md ("Viewer audio: getStats() is
 * not proof of audible sound"). This tries unmuted playback first (so sound just works whenever
 * the browser allows it), and falls back to muted autoplay - which is virtually always allowed -
 * only if that's rejected, surfacing a one-tap "unmute" affordance so a real user gesture (which
 * satisfies the browser's autoplay policy) can restore sound.
 */
async function startPlayback() {
  videoEl.muted = false;
  try {
    await videoEl.play();
    unmuteBtn.hidden = true;
  } catch {
    videoEl.muted = true;
    try {
      await videoEl.play();
    } catch (err) {
      console.warn('[viewer] video.play() failed even muted', err);
    }
    unmuteBtn.hidden = false;
  }
}

unmuteBtn.addEventListener('click', async () => {
  videoEl.muted = false;
  try {
    await videoEl.play();
    unmuteBtn.hidden = true; // only hide once actually confirmed playing unmuted
  } catch (err) {
    console.warn('[viewer] unmute play() failed even from a click', err);
    videoEl.muted = true;
    videoEl.play().catch(() => {}); // keep at least the picture moving, muted
  }
});

function send(message) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(message));
}

async function handleOffer(sdp, iceServers) {
  pc = createPeerConnection(iceServers);
  await pc.setRemoteDescription({ type: 'offer', sdp });
  const answer = await pc.createAnswer();
  await pc.setLocalDescription(answer);
  send({ type: 'answer', sdp: answer.sdp });
}

async function handleRemoteIceCandidate(candidate) {
  if (pc && candidate) {
    try {
      await pc.addIceCandidate(candidate);
    } catch (err) {
      console.warn('[viewer] failed to add ICE candidate', err);
    }
  }
}

function connect() {
  const sessionId = getSessionId();
  if (!sessionId) {
    terminal = true;
    setViewerState('SESSION_UNAVAILABLE');
    placeholderText.textContent = 'Missing session link.';
    return;
  }

  setViewerState(reconnectAttempt > 0 ? 'RECONNECTING' : 'CONNECTING');
  ws = new WebSocket(wsUrl(sessionId));

  ws.onmessage = async (event) => {
    const msg = JSON.parse(event.data);
    switch (msg.type) {
      case 'joined':
        serverClockOffsetMs = (msg.serverNow || Date.now()) - Date.now();
        sessionStartedAt = msg.startedAt || null;
        sessionExpiresAt = msg.expiresAt || null;
        startElapsedLoop();
        setViewerState('CONNECTING');
        break;
      case 'offer':
        await handleOffer(msg.sdp, msg.iceServers);
        break;
      case 'ice-candidate':
        await handleRemoteIceCandidate(msg.candidate);
        break;
      case 'session-ended':
        terminal = true;
        setViewerState('SESSION_ENDED');
        teardownPeerConnection();
        break;
      case 'session-expired':
        terminal = true;
        setViewerState('SESSION_EXPIRED');
        teardownPeerConnection();
        break;
      case 'error':
        terminal = true;
        setViewerState(errorReasonToState(msg.reason));
        break;
      default:
        break;
    }
  };

  ws.onclose = () => {
    if (terminal) return;
    setViewerState('RECONNECTING');
    stopStatsLoop();
    scheduleReconnect();
  };

  ws.onerror = () => {
    /* onclose follows every onerror for a WebSocket; reconnect is handled there */
  };
}

function errorReasonToState(reason) {
  switch (reason) {
    case 'session-expired':
      return 'SESSION_EXPIRED';
    case 'session-ended':
      return 'SESSION_ENDED';
    default:
      return 'SESSION_UNAVAILABLE'; // session-unavailable, session-full, or anything unrecognized
  }
}

function scheduleReconnect() {
  if (terminal || reconnectTimer) return;
  const delay = RECONNECT_DELAYS_MS[Math.min(reconnectAttempt, RECONNECT_DELAYS_MS.length - 1)];
  reconnectAttempt += 1;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    if (!terminal) connect();
  }, delay);
}

function teardownPeerConnection() {
  stopStatsLoop();
  stopElapsedLoop();
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  if (pc) {
    pc.close();
    pc = null;
  }
  if (ws) {
    ws.onclose = null; // this is an intentional close, not a drop to reconnect from
    ws.close();
    ws = null;
  }
  videoEl.srcObject = null;
  fullscreenBtn.disabled = true;
}

function startElapsedLoop() {
  stopElapsedLoop();
  if (!sessionStartedAt) return;
  const tick = () => {
    const now = Date.now() + serverClockOffsetMs;
    let elapsedMs = now - sessionStartedAt;
    if (elapsedMs < 0) elapsedMs = 0;
    const totalMs = sessionExpiresAt ? sessionExpiresAt - sessionStartedAt : null;
    elapsedEl.textContent = totalMs ? `${formatDuration(elapsedMs)} / ${formatDuration(totalMs)}` : formatDuration(elapsedMs);
  };
  tick();
  elapsedTimer = setInterval(tick, 1000);
}

function stopElapsedLoop() {
  if (elapsedTimer) {
    clearInterval(elapsedTimer);
    elapsedTimer = null;
  }
  elapsedEl.textContent = '';
}

function formatDuration(ms) {
  const totalSeconds = Math.floor(ms / 1000);
  const h = Math.floor(totalSeconds / 3600);
  const m = Math.floor((totalSeconds % 3600) / 60);
  const s = totalSeconds % 60;
  const pad = (n) => String(n).padStart(2, '0');
  return h > 0 ? `${pad(h)}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`;
}

function startStatsLoop() {
  stopStatsLoop();
  statsTimer = setInterval(async () => {
    if (!pc) return;
    const stats = await pc.getStats();
    let fps = null;
    let kbps = null;
    stats.forEach((report) => {
      if (report.type === 'inbound-rtp' && report.kind === 'video') {
        fps = report.framesPerSecond;
        if (report._prevBytes !== undefined) {
          kbps = Math.round(((report.bytesReceived - report._prevBytes) * 8) / 1000);
        }
      }
    });
    statsLine.textContent = fps ? `video ${fps.toFixed(0)} fps${kbps !== null ? `, ~${kbps} kbps` : ''}` : '';
  }, 1000);
}

function stopStatsLoop() {
  if (statsTimer) {
    clearInterval(statsTimer);
    statsTimer = null;
  }
  statsLine.textContent = '';
}

fullscreenBtn.addEventListener('click', () => {
  // Affects only this browser tab's rendering - never touches the WebSocket/RTCPeerConnection,
  // so entering/exiting fullscreen (including via Esc) can never end the session.
  if (videoEl.requestFullscreen) videoEl.requestFullscreen();
});

connect();
