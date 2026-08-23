'use strict';

const crypto = require('crypto');
const { SessionStatus, assertTransition, isJoinable } = require('./sessionStateMachine');
const { isValidDuration, computeExpiresAt } = require('./sessionDuration');

/**
 * In-memory session registry - deliberately not backed by a database. Sessions are ephemeral
 * (no screen/audio is ever stored, and a session record itself is worthless once the session
 * ends), a single signaling process is the whole of Phase 2's deployment target, and restarting
 * that process ending in-flight sessions is acceptable for this phase. See
 * docs/architecture/phase-2-technology-decision.md for the explicit "why not a database" call.
 */
class SessionStore {
  constructor(config) {
    this.config = config;
    /** @type {Map<string, Session>} */
    this.sessions = new Map();
  }

  /**
   * @param {WebSocket} hostSocket
   * @param {string} durationId one of sessionDuration.SESSION_DURATIONS' keys
   */
  createSession(hostSocket, durationId) {
    if (!isValidDuration(durationId)) {
      throw new Error(`invalid duration: ${durationId}`);
    }

    const id = generateSessionId();
    const hostToken = generateHostToken();
    const now = Date.now();

    const session = {
      id,
      hostToken,
      status: SessionStatus.CREATED,
      duration: durationId,
      hostSocket,
      viewers: new Map(), // viewerId -> { socket, joinedAt }
      createdAt: now,
      startedAt: null,
      expiresAt: null, // computed once the session actually goes live
      lastHostHeartbeatAt: now,
      expireTimer: null,
    };

    this.sessions.set(id, session);
    return session;
  }

  get(id) {
    return this.sessions.get(id);
  }

  /** Validates the caller actually owns this session before any host-only action proceeds. */
  authorizeHost(session, hostToken) {
    return !!session && session.hostToken === hostToken;
  }

  markStarting(session) {
    session.status = assertTransition(session.status, SessionStatus.STARTING);
  }

  markLive(session) {
    session.status = assertTransition(session.status, SessionStatus.LIVE);
    session.startedAt = Date.now();
    session.expiresAt = computeExpiresAt(session.duration, session.startedAt, this.config.testDurationOverrideMs);
  }

  markExpired(session) {
    session.status = assertTransition(session.status, SessionStatus.EXPIRED);
  }

  beginStopping(session) {
    if (session.status === SessionStatus.STOPPING || session.status === SessionStatus.STOPPED) return;
    session.status = assertTransition(session.status, SessionStatus.STOPPING);
  }

  markStopped(session) {
    session.status = assertTransition(session.status, SessionStatus.STOPPED);
  }

  recordHeartbeat(session) {
    session.lastHostHeartbeatAt = Date.now();
  }

  /** Throws if the session can't accept a viewer right now (state, or at capacity). */
  addViewer(session, socket) {
    if (!isJoinable(session.status)) {
      const err = new Error(`session not joinable in status ${session.status}`);
      err.code = 'NOT_JOINABLE';
      throw err;
    }
    if (session.viewers.size >= this.config.maxViewersPerSession) {
      const err = new Error('viewer capacity reached');
      err.code = 'CAPACITY';
      throw err;
    }
    const viewerId = generateViewerId();
    session.viewers.set(viewerId, { socket, joinedAt: Date.now() });
    return viewerId;
  }

  removeViewer(session, viewerId) {
    session.viewers.delete(viewerId);
  }

  removeSession(id) {
    this.sessions.delete(id);
  }

  /**
   * Called on a timer by the server. Returns the list of state-change events that happened this
   * tick so the caller can push the right WebSocket notifications - this module only owns state,
   * not socket I/O.
   * @returns {{ session: Session, reason: 'expired' | 'host-timeout' }[]}
   */
  runCleanupTick() {
    const now = Date.now();
    const events = [];

    for (const session of this.sessions.values()) {
      if (session.status === SessionStatus.LIVE && session.expiresAt !== null && now >= session.expiresAt) {
        this.markExpired(session);
        events.push({ session, reason: 'expired' });
        continue;
      }

      const isActive = session.status === SessionStatus.STARTING || session.status === SessionStatus.LIVE;
      if (isActive && now - session.lastHostHeartbeatAt > this.config.hostHeartbeatTimeoutMs) {
        events.push({ session, reason: 'host-timeout' });
        continue;
      }

      // Fully stopped sessions are removed promptly - there's nothing to keep them for
      // (no history is retained by design; see the class doc comment above).
      if (session.status === SessionStatus.STOPPED) {
        this.sessions.delete(session.id);
      }
    }

    return events;
  }

  get activeSessionCount() {
    let count = 0;
    for (const s of this.sessions.values()) {
      if (s.status !== SessionStatus.STOPPED) count++;
    }
    return count;
  }
}

function generateSessionId() {
  // 16 bytes -> ~22 base64url chars: not sequential, not guessable, and never used as a
  // filesystem/database key so there's no path-traversal concern either.
  return crypto.randomBytes(16).toString('base64url');
}

function generateHostToken() {
  return crypto.randomBytes(24).toString('base64url');
}

function generateViewerId() {
  return crypto.randomBytes(8).toString('hex');
}

module.exports = { SessionStore, generateSessionId, generateHostToken };
