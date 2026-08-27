'use strict';

const crypto = require('crypto');
const { SessionStatus, assertTransition, isJoinable } = require('./sessionStateMachine');
const { isValidDuration, computeExpiresAt } = require('./sessionDuration');
const { ControlSession, ControlSessionStatus } = require('./controlSession');

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
        /** @type {Map<string, ControlSession>} */
        this.controlSessions = new Map();
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
            hostDisconnectedAt: null, // set when the host's socket closes; cleared on successful resume
            controlSessions: new Map(), // controlSessionId -> ControlSession
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

    /** Called when the host's socket closes. Does not end the session immediately - see resumeHost. */
    markHostDisconnected(session) {
        session.hostDisconnectedAt = Date.now();
    }

    /**
     * Reattaches a new socket to an existing session after a host reconnect, iff it's still within
     * the grace period and not already ended. Returns false (does nothing) if the grace period has
     * already elapsed - the caller should treat that as "too late, session is already gone".
     */
    resumeHost(session, socket) {
        if (session.hostDisconnectedAt === null) return true; // wasn't actually disconnected
        const elapsed = Date.now() - session.hostDisconnectedAt;
        if (elapsed > this.config.hostReconnectGraceMs) return false;
        session.hostSocket = socket;
        session.hostDisconnectedAt = null;
        session.lastHostHeartbeatAt = Date.now();
        return true;
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
        const session = this.sessions.get(id);
        if (session) {
            // Clean up associated control sessions
            for (const controlSession of session.controlSessions.values()) {
                this.removeControlSession(controlSession.id);
            }
        }
        this.sessions.delete(id);
    }

    // Control Session Management

    createControlSession(screenSessionId, hostToken) {
        const session = this.get(screenSessionId);
        if (!session || !this.authorizeHost(session, hostToken)) {
            return null;
        }
        const controlSession = new ControlSession(screenSessionId, hostToken);
        session.controlSessions.set(controlSession.id, controlSession);
        this.controlSessions.set(controlSession.id, controlSession);
        return controlSession;
    }

    getControlSession(controlSessionId) {
        return this.controlSessions.get(controlSessionId);
    }

    getControlSessionByPairingCode(pairingCode) {
        for (const cs of this.controlSessions.values()) {
            if (cs.pairingCode === pairingCode) {
                return cs;
            }
        }
        return null;
    }

    removeControlSession(controlSessionId) {
        const controlSession = this.controlSessions.get(controlSessionId);
        if (controlSession) {
            const screenSession = this.get(controlSession.screenSessionId);
            if (screenSession) {
                screenSession.controlSessions.delete(controlSessionId);
            }
            this.controlSessions.delete(controlSessionId);
        }
    }

    authorizeControlSession(controlSessionId) {
        const controlSession = this.controlSessions.get(controlSessionId);
        if (!controlSession) return null;
        if (controlSession.authorize()) {
            return controlSession;
        }
        return null;
    }

    activateControlSession(controlSessionId, viewerSocket, viewerId) {
        const controlSession = this.controlSessions.get(controlSessionId);
        if (!controlSession) return null;
        if (controlSession.activate(viewerSocket, viewerId)) {
            return controlSession;
        }
        return null;
    }

    deactivateControlSession(controlSessionId) {
        const controlSession = this.controlSessions.get(controlSessionId);
        if (controlSession) {
            controlSession.deactivate();
        }
    }

    revokeControlSession(controlSessionId, hostToken) {
        const controlSession = this.controlSessions.get(controlSessionId);
        if (!controlSession) return false;
        const screenSession = this.get(controlSession.screenSessionId);
        if (!screenSession || !this.authorizeHost(screenSession, hostToken)) {
            return false;
        }
        controlSession.revoke();
        this.removeControlSession(controlSessionId);
        return true;
    }

    listControlSessions(screenSessionId, hostToken) {
        const session = this.get(screenSessionId);
        if (!session || !this.authorizeHost(session, hostToken)) {
            return [];
        }
        const result = [];
        for (const cs of session.controlSessions.values()) {
            result.push(cs.toHostView());
        }
        return result;
    }

    cleanupControlSessions() {
        const now = Date.now();
        for (const [id, cs] of this.controlSessions.entries()) {
            if (cs.isExpired() || (cs.isAuthorizationExpired() && cs.status === ControlSessionStatus.PENDING)) {
                this.removeControlSession(id);
            }
        }
    }

    /**
     * Called on a timer by the server. Returns the list of state-change events that happened this
     * tick so the caller can push the right WebSocket notifications - this module only owns state,
     * not socket I/O.
     * @returns {{ session: Session, reason: 'expired' | 'host-timeout' | 'host-disconnect-timeout' }[]}
     */
    runCleanupTick() {
        const now = Date.now();
        const events = [];

        // Clean up expired control sessions first
        this.cleanupControlSessions();

        for (const session of this.sessions.values()) {
            if (session.status === SessionStatus.LIVE && session.expiresAt !== null && now >= session.expiresAt) {
                this.markExpired(session);
                events.push({ session, reason: 'expired' });
                continue;
            }

            const isActive = session.status === SessionStatus.STARTING || session.status === SessionStatus.LIVE;

            // A host that disconnected but hasn't resumed within the grace period is treated the same
            // as any other unrecoverable host loss - reuses the same cleanup path as host-timeout.
            if (isActive && session.hostDisconnectedAt !== null && now - session.hostDisconnectedAt > this.config.hostReconnectGraceMs) {
                events.push({ session, reason: 'host-disconnect-timeout' });
                continue;
            }

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