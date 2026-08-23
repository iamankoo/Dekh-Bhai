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

    default:
      return { ok: false, reason: `unknown message type: ${msg.type}` };
  }
}

module.exports = { validateHostMessage, validateViewerMessage };
