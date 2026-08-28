'use strict';

/**
 * Dekh Bhai Remote Control Viewer.
 * 
 * Extends the basic viewer with remote control capabilities:
 *   - Touch-to-mouse interaction
 *   - Virtual keyboard
 *   - WebRTC DataChannel for control commands
 *   - Secure pairing/authorization flow
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
const touchOverlay = document.getElementById('touchOverlay');
const keyboardBtn = document.getElementById('keyboardBtn');
const leftClickBtn = document.getElementById('leftClickBtn');
const rightClickBtn = document.getElementById('rightClickBtn');
const disconnectBtn = document.getElementById('disconnectBtn');
const keyboardModal = document.getElementById('keyboardModal');
const closeKeyboardBtn = document.getElementById('closeKeyboardBtn');
const keyboardLayout = document.getElementById('keyboardLayout');

let pc = null;
let ws = null;
let controlDc = null;
let statsTimer = null;
let elapsedTimer = null;
let reconnectAttempt = 0;
let reconnectTimer = null;
let terminal = false;
let serverClockOffsetMs = 0;
let sessionStartedAt = null;
let sessionExpiresAt = null;
let controlSessionId = null;
let controlToken = null;
let viewerId = null;
// iceServers arrive on control-authorized, but the actual SDP offer only arrives later (a
// separate control-offer message sent once the host finishes setting up its side of the control
// peer connection - see WebRtcHost.CreateControlConnectionAsync) - held here so handleOffer has
// them when that later message arrives.
let pendingIceServers = [];

let mouseState = {
    x: 0,
    y: 0,
    leftDown: false,
    rightDown: false,
    lastClickTime: 0,
    clickCount: 0,
};

let keyboardModifiers = {
    shift: false,
    ctrl: false,
    alt: false,
    meta: false,
};

const VIEWER_STATE = {
    CONNECTING: { badge: 'Connecting…', kind: 'connecting', placeholder: 'Connecting to Dekh Bhai…' },
    WAITING_AUTH: { badge: 'Waiting for Authorization', kind: 'reconnecting', placeholder: 'Waiting for host to authorize…' },
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
    const pathMatch = window.location.pathname.match(/\/control\/([^/]+)/);
    if (pathMatch) return decodeURIComponent(pathMatch[1]);
    return new URLSearchParams(window.location.search).get('session');
}

function getPairingCode() {
    return new URLSearchParams(window.location.search).get('pairing');
}

function signalingOrigin() {
    const configured = window.DEKHBHAI_SIGNALING_ORIGIN;
    if (!configured) {
        const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        return `${proto}//${window.location.host}`;
    }
    return configured.replace(/^http:/, 'ws:').replace(/^https:/, 'wss:').replace(/\/$/, '');
}

function wsUrl(sessionId, pairingCode) {
    return `${signalingOrigin()}/ws?role=control&session=${encodeURIComponent(sessionId)}&pairing=${encodeURIComponent(pairingCode)}`;
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
                setupControlChannel();
                break;
            case 'disconnected':
                break;
            case 'failed':
            case 'closed':
                setViewerState('RECONNECTING');
                stopStatsLoop();
                scheduleReconnect();
                break;
        }
    };

    conn.ondatachannel = (event) => {
        const dc = event.channel;
        if (dc.label === 'control') {
            setupControlDataChannel(dc);
        }
    };

    return conn;
}

function setupControlDataChannel(dc) {
    controlDc = dc;
    controlDc.onmessage = (event) => {
        // Handle any responses from host if needed
        console.log('[control] Received:', event.data);
    };
    controlDc.onopen = () => {
        console.log('[control] DataChannel opened');
        touchOverlay.hidden = false;
        keyboardBtn.hidden = false;
        leftClickBtn.hidden = false;
        rightClickBtn.hidden = false;
    };
    controlDc.onclose = () => {
        console.log('[control] DataChannel closed');
        controlDc = null;
        touchOverlay.hidden = true;
        keyboardBtn.hidden = true;
        leftClickBtn.hidden = true;
        rightClickBtn.hidden = true;
    };
    controlDc.onerror = (err) => {
        console.error('[control] DataChannel error:', err);
    };
}

async function setupControlChannel() {
    if (pc && !controlDc) {
        try {
            controlDc = pc.createDataChannel('control', { ordered: true });
            setupControlDataChannel(controlDc);
        } catch (err) {
            console.error('[control] Failed to create DataChannel:', err);
        }
    }
}

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
        unmuteBtn.hidden = true;
    } catch (err) {
        console.warn('[viewer] unmute play() failed even from a click', err);
        videoEl.muted = true;
        videoEl.play().catch(() => {});
    }
});

function send(message) {
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(message));
}

function sendControl(message) {
    if (controlDc && controlDc.readyState === 'open') {
        controlDc.send(JSON.stringify(message));
    }
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
    const pairingCode = getPairingCode();

    if (!sessionId || !pairingCode) {
        terminal = true;
        setViewerState('SESSION_UNAVAILABLE');
        placeholderText.textContent = 'Missing session link or pairing code.';
        return;
    }

    setViewerState('CONNECTING');
    ws = new WebSocket(wsUrl(sessionId, pairingCode));

    ws.onmessage = async (event) => {
        const msg = JSON.parse(event.data);
        switch (msg.type) {
            case 'control-waiting-authorization':
                controlSessionId = msg.controlSessionId;
                setViewerState('WAITING_AUTH');
                break;
            case 'control-authorized':
                controlSessionId = msg.controlSessionId;
                controlToken = msg.controlToken;
                viewerId = msg.viewerId;
                serverClockOffsetMs = (msg.serverNow || Date.now()) - Date.now();
                sessionStartedAt = msg.startedAt || null;
                sessionExpiresAt = msg.expiresAt || null;
                startElapsedLoop();
                // No sdp here yet - this message only carries iceServers. The host doesn't start
                // building its control peer connection (and so doesn't have an offer to send)
                // until it separately learns this control viewer has joined; that offer arrives
                // as its own control-offer message, handled below.
                pendingIceServers = msg.iceServers || [];
                break;
            case 'offer':
                await handleOffer(msg.sdp, msg.iceServers);
                break;
            case 'control-offer':
                await handleOffer(msg.sdp, pendingIceServers);
                break;
            case 'ice-candidate':
                await handleRemoteIceCandidate(msg.candidate);
                break;
            case 'control-ice-candidate':
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
            case 'control-session-revoked':
                // Host revoked - control must stop immediately, not just on the next reconnect
                // attempt. releaseAllModifiers() before teardown so a modifier held at the exact
                // moment of revocation isn't left stuck client-side either.
                terminal = true;
                releaseAllModifiers();
                teardownPeerConnection();
                setViewerState('SESSION_ENDED');
                placeholderText.textContent = 'Remote control was revoked by the host.';
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
        case 'invalid-pairing-code':
        case 'missing-pairing-code':
        case 'control-session-expired':
        case 'control-session-revoked':
            return 'SESSION_UNAVAILABLE';
        default:
            return 'SESSION_UNAVAILABLE';
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
    if (controlDc) {
        controlDc.close();
        controlDc = null;
    }
    if (pc) {
        pc.close();
        pc = null;
    }
    if (ws) {
        ws.onclose = null;
        ws.close();
        ws = null;
    }
    videoEl.srcObject = null;
    fullscreenBtn.disabled = true;
    touchOverlay.hidden = true;
    keyboardBtn.hidden = true;
    leftClickBtn.hidden = true;
    rightClickBtn.hidden = true;
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
                report._prevBytes = report.bytesReceived;
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

// ============================================================
// Mouse Control
// ============================================================

let touchStartTime = 0;
let touchStartX = 0;
let touchStartY = 0;
let twoFingerLastY = 0;
let twoFingerMoved = false;

function getVideoRect() {
    return videoEl.getBoundingClientRect();
}

function getNormalizedCoords(clientX, clientY) {
    const rect = getVideoRect();
    const videoWidth = videoEl.videoWidth;
    const videoHeight = videoEl.videoHeight;
    
    if (!videoWidth || !videoHeight) return null;
    
    // Calculate the displayed video area (object-fit: contain)
    const scale = Math.min(rect.width / videoWidth, rect.height / videoHeight);
    const displayWidth = videoWidth * scale;
    const displayHeight = videoHeight * scale;
    const offsetX = (rect.width - displayWidth) / 2;
    const offsetY = (rect.height - displayHeight) / 2;
    
    const x = (clientX - rect.left - offsetX) / displayWidth;
    const y = (clientY - rect.top - offsetY) / displayHeight;
    
    // Clamp to [0, 1]
    return {
        x: Math.max(0, Math.min(1, x)),
        y: Math.max(0, Math.min(1, y)),
    };
}

function sendMouseMove(x, y) {
    sendControl({
        type: 'mouse_move',
        x: x,
        y: y,
    });
    mouseState.x = x;
    mouseState.y = y;
}

function sendMouseDown(button) {
    sendControl({
        type: 'mouse_down',
        button: button, // 'left', 'right', 'middle'
        x: mouseState.x,
        y: mouseState.y,
    });
    if (button === 'left') mouseState.leftDown = true;
    if (button === 'right') mouseState.rightDown = true;
}

function sendMouseUp(button) {
    sendControl({
        type: 'mouse_up',
        button: button,
        x: mouseState.x,
        y: mouseState.y,
    });
    if (button === 'left') mouseState.leftDown = false;
    if (button === 'right') mouseState.rightDown = false;
}

function sendMouseClick(button) {
    const now = Date.now();
    if (button === 'left') {
        if (now - mouseState.lastClickTime < 300 && mouseState.clickCount === 1) {
            mouseState.clickCount = 2;
            sendControl({
                type: 'mouse_double_click',
                x: mouseState.x,
                y: mouseState.y,
            });
            mouseState.lastClickTime = 0;
            mouseState.clickCount = 0;
            return;
        }
        mouseState.clickCount = 1;
        mouseState.lastClickTime = now;
    }
    sendControl({
        type: 'mouse_click',
        button: button,
        x: mouseState.x,
        y: mouseState.y,
    });
}

function sendScroll(deltaY) {
    sendControl({
        type: 'mouse_scroll',
        deltaY: deltaY,
        x: mouseState.x,
        y: mouseState.y,
    });
}

// Touch handling.
//
// One finger = move cursor; a quick tap-and-release with little movement is a left click, a
// slower one that moves is a drag (mouse stays down for the whole gesture, exactly like a real
// mouse button held while moving).
//
// Two fingers is ambiguous at touchstart - it becomes a right click only if the fingers lift
// again quickly without moving (a tap), or a scroll if they move first (a swipe). Right-click is
// therefore decided at touchend, and scrolling is driven live during touchmove.
touchOverlay.addEventListener('touchstart', (e) => {
    e.preventDefault();

    if (e.touches.length === 1) {
        const touch = e.touches[0];
        const coords = getNormalizedCoords(touch.clientX, touch.clientY);
        if (!coords) return;

        touchStartTime = Date.now();
        touchStartX = touch.clientX;
        touchStartY = touch.clientY;

        sendMouseMove(coords.x, coords.y);
        sendMouseDown('left');
    } else if (e.touches.length === 2) {
        // A left button held from a preceding single-finger touch must not bleed into a
        // two-finger gesture as an accidental drag.
        if (mouseState.leftDown) sendMouseUp('left');

        touchStartTime = Date.now();
        twoFingerLastY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
        twoFingerMoved = false;
    }

    touchOverlay._lastTouchCount = e.touches.length;
}, { passive: false });

touchOverlay.addEventListener('touchmove', (e) => {
    e.preventDefault();

    if (e.touches.length === 1) {
        const touch = e.touches[0];
        const coords = getNormalizedCoords(touch.clientX, touch.clientY);
        if (!coords) return;
        sendMouseMove(coords.x, coords.y);
    } else if (e.touches.length === 2) {
        const currentY = (e.touches[0].clientY + e.touches[1].clientY) / 2;
        const deltaY = twoFingerLastY - currentY; // finger moves up -> content scrolls down
        if (Math.abs(deltaY) > 2) {
            twoFingerMoved = true;
            sendScroll(deltaY / 4); // matches roughly one wheel "notch" per ~4px of finger travel
            twoFingerLastY = currentY;
        }
    }
}, { passive: false });

touchOverlay.addEventListener('touchend', (e) => {
    e.preventDefault();
    const lastCount = touchOverlay._lastTouchCount;
    const touchDuration = Date.now() - touchStartTime;

    if (lastCount === 1) {
        const touchDistance = Math.hypot(
            (e.changedTouches[0]?.clientX || touchStartX) - touchStartX,
            (e.changedTouches[0]?.clientY || touchStartY) - touchStartY
        );
        if (mouseState.leftDown) sendMouseUp('left');
        if (touchDuration < 300 && touchDistance < 20) {
            sendMouseClick('left');
        }
    } else if (lastCount === 2) {
        // Only a tap (no scroll movement happened) resolves to a right click.
        if (!twoFingerMoved && touchDuration < 300) {
            sendMouseDown('right');
            sendMouseUp('right');
        }
    }

    touchOverlay._lastTouchCount = e.touches.length;
}, { passive: false });

touchOverlay.addEventListener('touchcancel', (e) => {
    e.preventDefault();
    if (mouseState.leftDown) sendMouseUp('left');
    if (mouseState.rightDown) sendMouseUp('right');
    touchOverlay._lastTouchCount = e.touches.length;
}, { passive: false });

// Mouse button controls
leftClickBtn.addEventListener('pointerdown', () => sendMouseDown('left'));
leftClickBtn.addEventListener('pointerup', () => { sendMouseUp('left'); sendMouseClick('left'); });
leftClickBtn.addEventListener('pointerleave', () => { if (mouseState.leftDown) sendMouseUp('left'); });

rightClickBtn.addEventListener('pointerdown', () => sendMouseDown('right'));
rightClickBtn.addEventListener('pointerup', () => { sendMouseUp('right'); sendMouseClick('right'); });
rightClickBtn.addEventListener('pointerleave', () => { if (mouseState.rightDown) sendMouseUp('right'); });

// Scroll handling
touchOverlay.addEventListener('wheel', (e) => {
    e.preventDefault();
    sendScroll(e.deltaY);
}, { passive: false });

// ============================================================
// Keyboard Control
// ============================================================

const KEY_LAYOUT = [
    ['`', '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=', 'Backspace'],
    ['Tab', 'Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P', '[', ']', '\\'],
    ['CapsLock', 'A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L', ';', "'", 'Enter'],
    ['Shift', 'Z', 'X', 'C', 'V', 'B', 'N', 'M', ',', '.', '/', 'Shift'],
    ['Ctrl', 'Win', 'Alt', 'Space', 'Alt', 'Ctrl', '←', '↑', '↓', '→'],
];

function createKeyboard() {
    keyboardLayout.innerHTML = '';
    
    KEY_LAYOUT.forEach((rowKeys) => {
        const row = document.createElement('div');
        row.className = 'keyboard-row';
        
        rowKeys.forEach((key) => {
            const btn = document.createElement('button');
            btn.className = 'key-btn';
            btn.textContent = key;
            btn.dataset.key = key;
            
            if (['Tab', 'CapsLock', 'Enter', 'Backspace', 'Shift', 'Ctrl', 'Win', 'Alt'].includes(key)) {
                btn.classList.add('wide');
            }
            if (key === 'Space') {
                btn.classList.add('extra-wide');
                btn.style.flexGrow = '1';
            }
            
            btn.addEventListener('pointerdown', () => handleKeyDown(key));
            btn.addEventListener('pointerup', () => handleKeyUp(key));
            btn.addEventListener('pointerleave', () => handleKeyUp(key));
            
            row.appendChild(btn);
        });
        
        keyboardLayout.appendChild(row);
    });
    
    // Update modifier button states
    updateModifierButtons();
}

function updateModifierButtons() {
    document.querySelectorAll('.key-btn[data-key]').forEach((btn) => {
        const key = btn.dataset.key.toLowerCase();
        if (['shift', 'ctrl', 'alt', 'meta', 'capslock'].includes(key)) {
            const isActive = keyboardModifiers[key] || (key === 'capslock' && keyboardModifiers.capslock);
            btn.classList.toggle('modifier', true);
            btn.classList.toggle('active', isActive);
        }
    });
}

function handleKeyDown(key) {
    const normalizedKey = normalizeKey(key);
    if (!normalizedKey) return;
    
    // Handle modifiers
    if (['Shift', 'Ctrl', 'Alt', 'Meta', 'CapsLock'].includes(key)) {
        const modKey = key.toLowerCase();
        if (modKey === 'capslock') {
            keyboardModifiers.capslock = !keyboardModifiers.capslock;
        } else {
            keyboardModifiers[modKey] = true;
        }
        updateModifierButtons();
        sendControl({
            type: 'keyboard_down',
            key: normalizedKey,
            modifiers: { ...keyboardModifiers },
        });
        return;
    }
    
    // Regular keys
    let char = key;
    if (keyboardModifiers.shift && key.length === 1) {
        char = key.toUpperCase();
    } else if (keyboardModifiers.capslock && key.length === 1 && /[a-z]/i.test(key)) {
        char = keyboardModifiers.shift ? key.toLowerCase() : key.toUpperCase();
    }
    
    if (char.length === 1) {
        // Printable character - send as text input
        sendControl({
            type: 'text_input',
            text: char,
        });
    } else {
        // Special key
        sendControl({
            type: 'keyboard_down',
            key: normalizedKey,
            modifiers: { ...keyboardModifiers },
        });
    }
}

function handleKeyUp(key) {
    const normalizedKey = normalizeKey(key);
    if (!normalizedKey) return;
    
    if (['Shift', 'Ctrl', 'Alt', 'Meta'].includes(key)) {
        const modKey = key.toLowerCase();
        keyboardModifiers[modKey] = false;
        updateModifierButtons();
        sendControl({
            type: 'keyboard_up',
            key: normalizedKey,
            modifiers: { ...keyboardModifiers },
        });
    } else if (!['CapsLock', 'Tab', 'Enter', 'Backspace', 'Space'].includes(key)) {
        // For non-modifier special keys, send key up
        sendControl({
            type: 'keyboard_up',
            key: normalizedKey,
            modifiers: { ...keyboardModifiers },
        });
    }
}

function normalizeKey(key) {
    const map = {
        'Backspace': 'Backspace',
        'Tab': 'Tab',
        'Enter': 'Enter',
        'Shift': 'Shift',
        'Ctrl': 'Control',
        'Alt': 'Alt',
        'Meta': 'Meta',
        'Win': 'Meta',
        'CapsLock': 'CapsLock',
        'Space': ' ',
        '←': 'ArrowLeft',
        '↑': 'ArrowUp',
        '↓': 'ArrowDown',
        '→': 'ArrowRight',
    };
    return map[key] || key;
}

// ============================================================
// Physical keyboard passthrough
// ============================================================
//
// The on-screen keyboard above exists for touch devices with no keyboard of their own. A
// controller on a laptop/desktop already has a real keyboard, so its actual keydown/keyup events
// are forwarded directly - no need to hunt-and-peck the virtual keyboard just because the phone
// UI happens to have one. Active for the whole page any time the control channel is open.
//
// e.key already reflects the browser/OS's own shift + caps-lock + keyboard-layout handling (e.g.
// Shift+a arrives as 'A', Shift+; arrives as ':'), so a plain printable character is forwarded
// as-is via text_input (which also transparently supports non-ASCII characters from any keyboard
// layout) - it does not need shift simulated separately. Only Ctrl/Alt/Meta shortcuts and
// non-printable keys (arrows, function keys, Enter, ...) go through the down/up keyboard_* path
// InputInjector uses for held keys and modifier combinations like Ctrl+C.

function isModifierKeyName(key) {
    return key === 'Shift' || key === 'Control' || key === 'Alt' || key === 'Meta' || key === 'AltGraph';
}

document.addEventListener('keydown', (e) => {
    if (!controlDc || controlDc.readyState !== 'open') return;
    e.preventDefault();

    const key = e.key === 'AltGraph' ? 'Alt' : e.key;
    if (isModifierKeyName(key)) {
        sendControl({ type: 'keyboard_down', key });
        return;
    }

    const isPrintable = key.length === 1;
    if (isPrintable && !e.ctrlKey && !e.altKey && !e.metaKey) {
        sendControl({ type: 'text_input', text: key });
        return;
    }

    // A non-printable special key, or a Ctrl/Alt/Meta shortcut (e.g. Ctrl+C) - InputInjector
    // presses the requested modifiers before the key itself.
    sendControl({
        type: 'keyboard_down',
        key,
        modifiers: { shift: e.shiftKey, ctrl: e.ctrlKey, alt: e.altKey, meta: e.metaKey },
    });
});

document.addEventListener('keyup', (e) => {
    if (!controlDc || controlDc.readyState !== 'open') return;
    e.preventDefault();

    const key = e.key === 'AltGraph' ? 'Alt' : e.key;
    if (isModifierKeyName(key)) {
        sendControl({ type: 'keyboard_up', key });
        return;
    }

    const isPrintable = key.length === 1;
    if (isPrintable && !e.ctrlKey && !e.altKey && !e.metaKey) {
        return; // text_input on keydown already completed a full press+release host-side
    }

    // Deliberately no modifiers here - releasing e.g. 'c' from a still-held Ctrl+C must not also
    // release Ctrl (the physical Control key is still down on the controller's keyboard until its
    // own keyup fires above).
    sendControl({ type: 'keyboard_up', key });
});

// Keyboard modal
keyboardBtn.addEventListener('click', () => {
    keyboardModal.hidden = false;
    createKeyboard();
});

closeKeyboardBtn.addEventListener('click', () => {
    keyboardModal.hidden = true;
    // Release all modifiers when closing keyboard
    releaseAllModifiers();
});

keyboardModal.addEventListener('click', (e) => {
    if (e.target === keyboardModal) {
        keyboardModal.hidden = true;
        releaseAllModifiers();
    }
});

function releaseAllModifiers() {
    const wasActive = keyboardModifiers.shift || keyboardModifiers.ctrl || keyboardModifiers.alt || keyboardModifiers.meta;
    keyboardModifiers = { shift: false, ctrl: false, alt: false, meta: false };
    if (wasActive) {
        // Send key up for all modifiers that were active
        ['Shift', 'Control', 'Alt', 'Meta'].forEach((key) => {
            sendControl({
                type: 'keyboard_up',
                key: key,
                modifiers: { ...keyboardModifiers },
            });
        });
    }
    updateModifierButtons();
}

// ============================================================
// Fullscreen & Disconnect
// ============================================================

fullscreenBtn.addEventListener('click', () => {
    if (videoEl.requestFullscreen) videoEl.requestFullscreen();
});

disconnectBtn.addEventListener('click', () => {
    terminal = true;
    teardownPeerConnection();
    setViewerState('SESSION_ENDED');
    placeholderText.textContent = 'Disconnected.';
});

// Cleanup on page unload
window.addEventListener('beforeunload', () => {
    releaseAllModifiers();
    teardownPeerConnection();
});

connect();