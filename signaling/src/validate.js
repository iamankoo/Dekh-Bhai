'use strict';

const { isValidDuration } = require('./sessionDuration');

// Generous but bounded - SDP blobs and ICE candidate strings are small in practice; this just
// stops a malformed/malicious client from sending an unbounded string into memory.
const MAX_STRING_LENGTH = 64 * 1024;

function isBoundedString(v) {
    return typeof v === 'string' && v.length > 0 && v.length <= MAX_STRING_LENGTH;
}

function isShortToken(v) {
    return typeof v === 'string' && v.length > 0 && v.length <= 256;
}

function isPairingCode(v) {
    return typeof v === 'string' && /^\d{4}-\d{4}$/.test(v);
}

function isControlToken(v) {
    return typeof v === 'string' && v.length > 0 && v.length <= 512;
}

/**
 * Per-message-type shape validation for everything a host connection may send. Returns
 * { ok: true } or { ok: false, reason }. Nothing here throws - malformed input is always a
 * rejected message, never a crash, and the caller decides what (if anything) to tell the client.
 */
function validateHostMessage(msg) {
    if (!msg || typeof msg !== 'object' || typeof msg.type !== 'string') {
        return { ok: false, reason: 'missing or invalid type' };
    }

    switch (msg.type) {
        case 'create-session':
            if (!isValidDuration(msg.duration)) return { ok: false, reason: 'invalid duration' };
            return { ok: true };

        case 'host-live':
        case 'stop-session':
        case 'heartbeat':
            if (!isShortToken(msg.hostToken)) return { ok: false, reason: 'missing hostToken' };
            return { ok: true };

        case 'resume-session':
            if (!isShortToken(msg.sessionId)) return { ok: false, reason: 'missing sessionId' };
            if (!isShortToken(msg.hostToken)) return { ok: false, reason: 'missing hostToken' };
            return { ok: true };

        case 'offer':
            if (!isShortToken(msg.hostToken)) return { ok: false, reason: 'missing hostToken' };
            if (!isShortToken(msg.viewerId)) return { ok: false, reason: 'missing viewerId' };
            if (!isBoundedString(msg.sdp)) return { ok: false, reason: 'missing/invalid sdp' };
            return { ok: true };

        case 'ice-candidate':
            if (!isShortToken(msg.hostToken)) return { ok: false, reason: 'missing hostToken' };
            if (!isShortToken(msg.viewerId)) return { ok: false, reason: 'missing viewerId' };
            if (!msg.candidate || typeof msg.candidate !== 'object') {
                return { ok: false, reason: 'missing/invalid candidate' };
            }
            if (!isBoundedString(msg.candidate.candidate)) return { ok: false, reason: 'invalid candidate.candidate' };
            return { ok: true };

        // Control session messages
        case 'create-control-session':
            if (!isShortToken(msg.hostToken)) return { ok: false, reason: 'missing hostToken' };
            return { ok: true };

        case 'authorize-control-session':
            if (!isShortToken(msg.hostToken)) return { ok: false, reason: 'missing hostToken' };
            if (!isShortToken(msg.controlSessionId)) return { ok: false, reason: 'missing controlSessionId' };
            return { ok: true };

        case 'revoke-control-session':
        case 'deny-control-session':
            if (!isShortToken(msg.hostToken)) return { ok: false, reason: 'missing hostToken' };
            if (!isShortToken(msg.controlSessionId)) return { ok: false, reason: 'missing controlSessionId' };
            return { ok: true };

        case 'list-control-sessions':
            if (!isShortToken(msg.hostToken)) return { ok: false, reason: 'missing hostToken' };
            return { ok: true };

        // The host's SDP offer/ICE candidates for the control WebRTC connection (see
        // WebRtcHost.CreateControlConnectionAsync) - forwarded to the control viewer by the
        // 'control-offer'/'control-ice-candidate' case in server.js. Without these two cases that
        // forwarding code was unreachable: every offer and ICE candidate the host sent for a
        // control connection was rejected here first as an "unknown message type" and the control
        // viewer's page was left stuck on "Connecting..." forever, with no video/data channel ever
        // established, no matter how correct the rest of the control plumbing was.
        case 'control-offer':
            if (!isShortToken(msg.hostToken)) return { ok: false, reason: 'missing hostToken' };
            if (!isShortToken(msg.controlSessionId)) return { ok: false, reason: 'missing controlSessionId' };
            if (!isBoundedString(msg.sdp)) return { ok: false, reason: 'missing/invalid sdp' };
            return { ok: true };

        case 'control-ice-candidate':
            if (!isShortToken(msg.hostToken)) return { ok: false, reason: 'missing hostToken' };
            if (!isShortToken(msg.controlSessionId)) return { ok: false, reason: 'missing controlSessionId' };
            if (!msg.candidate || typeof msg.candidate !== 'object') {
                return { ok: false, reason: 'missing/invalid candidate' };
            }
            if (!isBoundedString(msg.candidate.candidate)) return { ok: false, reason: 'invalid candidate.candidate' };
            return { ok: true };

        default:
            return { ok: false, reason: `unknown message type: ${msg.type}` };
    }
}

/** Everything a viewer connection may send - deliberately a much smaller surface. */
function validateViewerMessage(msg) {
    if (!msg || typeof msg !== 'object' || typeof msg.type !== 'string') {
        return { ok: false, reason: 'missing or invalid type' };
    }

    switch (msg.type) {
        case 'answer':
            if (!isBoundedString(msg.sdp)) return { ok: false, reason: 'missing/invalid sdp' };
            return { ok: true };

        case 'ice-candidate':
            if (!msg.candidate || typeof msg.candidate !== 'object') {
                return { ok: false, reason: 'missing/invalid candidate' };
            }
            if (!isBoundedString(msg.candidate.candidate)) return { ok: false, reason: 'invalid candidate.candidate' };
            return { ok: true };

        // Sent by the normal viewer's "Remote Control" button - no extra fields, the session is
        // already implied by this socket.
        case 'request-control':
            return { ok: true };

        // Control viewer messages
        case 'control-join':
            if (!isShortToken(msg.sessionId)) return { ok: false, reason: 'missing sessionId' };
            if (!isPairingCode(msg.pairingCode)) return { ok: false, reason: 'invalid pairingCode' };
            return { ok: true };

        case 'control-authorize':
            if (!isShortToken(msg.controlSessionId)) return { ok: false, reason: 'missing controlSessionId' };
            if (!isControlToken(msg.controlToken)) return { ok: false, reason: 'invalid controlToken' };
            return { ok: true };

        case 'control-offer':
            if (!isShortToken(msg.controlSessionId)) return { ok: false, reason: 'missing controlSessionId' };
            if (!isControlToken(msg.controlToken)) return { ok: false, reason: 'invalid controlToken' };
            if (!isBoundedString(msg.sdp)) return { ok: false, reason: 'missing/invalid sdp' };
            return { ok: true };

        case 'control-ice-candidate':
            if (!isShortToken(msg.controlSessionId)) return { ok: false, reason: 'missing controlSessionId' };
            if (!isControlToken(msg.controlToken)) return { ok: false, reason: 'invalid controlToken' };
            if (!msg.candidate || typeof msg.candidate !== 'object') {
                return { ok: false, reason: 'missing/invalid candidate' };
            }
            if (!isBoundedString(msg.candidate.candidate)) return { ok: false, reason: 'invalid candidate.candidate' };
            return { ok: true };

        default:
            return { ok: false, reason: `unknown message type: ${msg.type}` };
    }
}

module.exports = { validateHostMessage, validateViewerMessage };