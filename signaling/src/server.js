'use strict';

const http = require('http');
const path = require('path');
const express = require('express');
const { WebSocketServer } = require('ws');

const config = require('./config');
const logger = require('./logger');
const { SessionStore } = require('./sessionStore');
const { SessionStatus } = require('./sessionStateMachine');
const { validateHostMessage, validateViewerMessage } = require('./validate');
const { buildIceServers } = require('./turnCredentials');
const { RateLimiter } = require('./rateLimiter');

const app = express();
const viewerDir = path.join(__dirname, '..', '..', 'viewer');

// viewer/config.js is checked in with the PRODUCTION signaling origin (see
// docs/deployment/phase-2.md) so the Vercel deployment - which serves that file as a plain
// static asset - talks to the deployed Render signaling server. This signaling process also
// happens to serve the same viewer/ directory for local development (see docs/development/
// setup.md), which would otherwise mean a locally-run viewer also points itself at production
// instead of this same-origin dev server. This explicit route (registered before the static
// middleware below, so it always wins for this one path) overrides that specific file for
// anything served BY this process - the checked-in production value is only ever seen by
// Vercel's independent static deployment, never by this dev server. Found and fixed during
// Phase 3 testing (see docs/architecture/phase-3-technology-decision.md) - resume-session
// testing against a local session surfaced that the viewer was silently trying to reach
// production instead of localhost.
app.get('/config.js', (_req, res) => {
    res.type('application/javascript').send(`window.DEKHBHAI_SIGNALING_ORIGIN = '${signalingOrigin}';\n`);
});

app.use(express.static(viewerDir));

// Production viewer URLs are path-based (https://<domain>/v/<sessionId>) rather than a query
// string, per the Phase 2 brief - serve the same static viewer page there; viewer.js reads the
// session id back out of the path.
app.get('/v/:sessionId', (_req, res) => res.sendFile(path.join(viewerDir, 'index.html')));

// Control viewer URLs - separate path for remote control interface
app.get('/control/:sessionId', (_req, res) => res.sendFile(path.join(viewerDir, 'control.html')));

app.get('/healthz', (_req, res) => res.json({ ok: true })); // kept for Phase 1 compatibility
app.get('/health', (_req, res) =>
    res.json({ status: 'ok', uptimeSeconds: Math.floor(process.uptime()), activeSessions: store.activeSessionCount })
);
app.get('/ready', (_req, res) => res.json({ ready: true }));

const server = http.createServer(app);
const wss = new WebSocketServer({ server, path: '/ws' });
const store = new SessionStore(config);
const rateLimiter = new RateLimiter({ windowMs: config.rateLimitWindowMs, maxMessages: config.rateLimitMaxMessages });

// Get LAN IP for LAN test mode
function getLanIp() {
    const interfaces = require('os').networkInterfaces();
    for (const name of Object.keys(interfaces)) {
        for (const iface of interfaces[name]) {
            if (iface.family === 'IPv4' && !iface.internal) {
                return iface.address;
            }
        }
    }
    return '127.0.0.1';
}

const lanIp = config.isLanTest ? getLanIp() : '127.0.0.1';
const signalingOrigin = config.isLanTest ? `http://${lanIp}:${config.port}` : `http://localhost:${config.port}`;
const viewerBaseUrl = config.isLanTest ? `http://${lanIp}:${config.port}/` : `http://localhost:${config.port}/`;

function send(socket, message) {
    if (socket && socket.readyState === socket.OPEN) {
        socket.send(JSON.stringify(message));
    }
}

function sendError(socket, reason) {
    send(socket, { type: 'error', reason });
}

function notifyViewers(session, message) {
    for (const { socket } of session.viewers.values()) send(socket, message);
}

function closeAllViewers(session, code, reason) {
    for (const { socket } of session.viewers.values()) {
        try {
            socket.close(code, reason);
        } catch {
            /* best-effort */
        }
    }
}

function notifyControlViewers(session, message) {
    for (const controlSession of session.controlSessions.values()) {
        if (controlSession.status === 'ACTIVE' && controlSession.viewerSocket) {
            send(controlSession.viewerSocket, message);
        }
    }
}

wss.on('connection', (socket, request) => {
    const url = new URL(request.url, `http://${request.headers.host}`);
    const role = url.searchParams.get('role');
    const tracker = rateLimiter.createTracker();

    if (role === 'host') {
        handleHost(socket, tracker);
    } else if (role === 'viewer') {
        handleViewer(socket, url, tracker);
    } else if (role === 'control') {
        handleControlViewer(socket, url, tracker);
    } else {
        socket.close(4000, 'role query param must be "host", "viewer", or "control"');
    }
});

function handleHost(socket, tracker) {
    /** @type {import('./sessionStore').Session | null} */
    let session = null;

    socket.on('message', (raw) => {
        if (!tracker.allow()) {
            sendError(socket, 'rate-limited');
            return;
        }

        let msg;
        try {
            msg = JSON.parse(raw.toString());
        } catch {
            sendError(socket, 'malformed-json');
            return;
        }

        const validation = validateHostMessage(msg);
        if (!validation.ok) {
            logger.warn('host.invalid-message', { reason: validation.reason, type: msg && msg.type });
            sendError(socket, 'invalid-message');
            return;
        }

        if (msg.type === 'create-session') {
            if (session) {
                sendError(socket, 'session-already-created-on-this-connection');
                return;
            }
            session = store.createSession(socket, msg.duration);
            store.markStarting(session);
            logger.info('session.created', { sessionId: session.id, duration: session.duration });
            send(socket, {
                type: 'session-created',
                sessionId: session.id,
                hostToken: session.hostToken,
                duration: session.duration,
                iceServers: buildIceServers(config, `host:${session.id}`),
            });
            return;
        }

        if (msg.type === 'resume-session') {
            if (session) {
                sendError(socket, 'session-already-created-on-this-connection');
                return;
            }
            const existing = store.get(msg.sessionId);
            if (!existing || !store.authorizeHost(existing, msg.hostToken)) {
                sendError(socket, 'unauthorized');
                return;
            }
            if (existing.status === SessionStatus.STOPPED || existing.status === SessionStatus.EXPIRED) {
                sendError(socket, 'session-ended');
                return;
            }
            if (!store.resumeHost(existing, socket)) {
                sendError(socket, 'session-ended');
                return;
            }
            session = existing;
            logger.info('session.host-resumed', { sessionId: session.id, status: session.status });
            send(socket, {
                type: 'resumed',
                sessionId: session.id,
                status: session.status,
                startedAt: session.startedAt,
                expiresAt: session.expiresAt,
                iceServers: buildIceServers(config, `host:${session.id}`),
            });
            return;
        }

        // Every other host message must reference a session this exact connection owns.
        if (!session || !store.authorizeHost(session, msg.hostToken) || session.hostSocket !== socket) {
            sendError(socket, 'unauthorized');
            return;
        }

        switch (msg.type) {
            case 'host-live':
                try {
                    store.markLive(session);
                } catch (err) {
                    sendError(socket, 'invalid-state');
                    return;
                }
                logger.info('session.live', { sessionId: session.id, expiresAt: session.expiresAt });
                send(socket, { type: 'live-ack', startedAt: session.startedAt, expiresAt: session.expiresAt });
                break;

            case 'heartbeat':
                store.recordHeartbeat(session);
                break;

            case 'stop-session':
                endSession(session, 'stopped', 'host requested stop');
                break;

            case 'offer':
            case 'ice-candidate': {
                const viewer = session.viewers.get(msg.viewerId);
                if (viewer) send(viewer.socket, { ...msg, hostToken: undefined });
                break;
            }

            // Control session management
            case 'create-control-session': {
                const controlSession = store.createControlSession(session.id, msg.hostToken);
                if (!controlSession) {
                    sendError(socket, 'unauthorized');
                    return;
                }
                logger.info('control-session.created', { sessionId: session.id, controlSessionId: controlSession.id });
                send(socket, {
                    type: 'control-session-created',
                    controlSessionId: controlSession.id,
                    pairingCode: controlSession.pairingCode,
                    controlToken: controlSession.controlToken,
                    expiresAt: controlSession.expiresAt,
                });
                break;
            }

            case 'authorize-control-session': {
                const controlSession = store.authorizeControlSession(msg.controlSessionId);
                if (!controlSession) {
                    sendError(socket, 'control-session-not-found-or-expired');
                    return;
                }
                logger.info('control-session.authorized', { sessionId: session.id, controlSessionId: controlSession.id });
                send(socket, {
                    type: 'control-session-authorized',
                    controlSessionId: controlSession.id,
                });
                // Tell whichever viewer asked for this (via the normal viewer's "Remote Control"
                // button) that the host said yes, so it can open the control connection.
                if (controlSession.requesterSocket) {
                    send(controlSession.requesterSocket, {
                        type: 'control-request-approved',
                        controlSessionId: controlSession.id,
                        pairingCode: controlSession.pairingCode,
                    });
                }
                // Back-compat: a control viewer that connected directly to /control/:id and is
                // already waiting on this exact socket (rather than going through request-control
                // above) gets activated immediately too.
                if (controlSession.viewerSocket && controlSession.status === 'AUTHORIZED') {
                    send(controlSession.viewerSocket, {
                        type: 'control-authorized',
                        controlSessionId: controlSession.id,
                        iceServers: buildIceServers(config, `control:${controlSession.id}`),
                    });
                }
                break;
            }

            case 'deny-control-session': {
                const controlSession = store.getControlSession(msg.controlSessionId);
                if (!controlSession || controlSession.screenSessionId !== session.id) {
                    sendError(socket, 'control-session-not-found-or-unauthorized');
                    return;
                }
                logger.info('control-session.denied', { sessionId: session.id, controlSessionId: msg.controlSessionId });
                if (controlSession.requesterSocket) {
                    send(controlSession.requesterSocket, {
                        type: 'control-request-denied',
                        controlSessionId: controlSession.id,
                    });
                }
                store.removeControlSession(controlSession.id);
                break;
            }

            case 'revoke-control-session': {
                const controlSession = store.getControlSession(msg.controlSessionId);
                const success = store.revokeControlSession(msg.controlSessionId, msg.hostToken);
                if (!success) {
                    sendError(socket, 'control-session-not-found-or-unauthorized');
                    return;
                }
                logger.info('control-session.revoked', { sessionId: session.id, controlSessionId: msg.controlSessionId });
                if (controlSession && controlSession.viewerSocket) {
                    send(controlSession.viewerSocket, {
                        type: 'control-session-revoked',
                        controlSessionId: msg.controlSessionId,
                    });
                }
                send(socket, {
                    type: 'control-session-revoked',
                    controlSessionId: msg.controlSessionId,
                });
                break;
            }

            case 'list-control-sessions': {
                const controlSessions = store.listControlSessions(session.id, msg.hostToken);
                send(socket, {
                    type: 'control-sessions-list',
                    controlSessions,
                });
                break;
            }

            case 'control-offer':
            case 'control-ice-candidate': {
                // Forward control signaling to the control viewer
                for (const controlSession of session.controlSessions.values()) {
                    if (controlSession.id === msg.controlSessionId && controlSession.viewerSocket) {
                        send(controlSession.viewerSocket, { ...msg, hostToken: undefined });
                    }
                }
                break;
            }
        }
    });

    socket.on('close', () => {
        if (session && session.status !== SessionStatus.STOPPED && session.hostSocket === socket) {
            logger.info('session.host-disconnected', {
                sessionId: session.id,
                status: session.status,
                graceMs: config.hostReconnectGraceMs,
            });
            store.markHostDisconnected(session);
        }
    });

    socket.on('error', (err) => logger.warn('host.socket-error', { message: err.message }));
}

function handleViewer(socket, url, tracker) {
    const sessionId = url.searchParams.get('session');
    const session = sessionId ? store.get(sessionId) : null;

    if (!session) {
        sendError(socket, 'session-unavailable');
        socket.close(4004, 'session not found');
        return;
    }
    if (session.status === SessionStatus.EXPIRED) {
        sendError(socket, 'session-expired');
        socket.close(4004, 'session expired');
        return;
    }
    if (session.status === SessionStatus.STOPPING || session.status === SessionStatus.STOPPED) {
        sendError(socket, 'session-ended');
        socket.close(4004, 'session ended');
        return;
    }
    if (session.status !== SessionStatus.LIVE) {
        sendError(socket, 'session-unavailable');
        socket.close(4004, 'session not live yet');
        return;
    }

    let viewerId;
    try {
        viewerId = store.addViewer(session, socket);
    } catch (err) {
        sendError(socket, err.code === 'CAPACITY' ? 'session-full' : 'session-unavailable');
        socket.close(4004, err.message);
        return;
    }

    logger.info('viewer.joined', { sessionId: session.id, viewerId, viewerCount: session.viewers.size });
    send(socket, {
        type: 'joined',
        viewerId,
        sessionId: session.id,
        iceServers: buildIceServers(config, `viewer:${viewerId}`),
        startedAt: session.startedAt,
        expiresAt: session.expiresAt,
        serverNow: Date.now(),
    });
    send(session.hostSocket, { type: 'viewer-joined', viewerId, viewerCount: session.viewers.size });

    socket.on('message', (raw) => {
        if (!tracker.allow()) {
            sendError(socket, 'rate-limited');
            return;
        }

        let msg;
        try {
            msg = JSON.parse(raw.toString());
        } catch {
            sendError(socket, 'malformed-json');
            return;
        }

        const validation = validateViewerMessage(msg);
        if (!validation.ok) {
            logger.warn('viewer.invalid-message', { reason: validation.reason, type: msg && msg.type });
            sendError(socket, 'invalid-message');
            return;
        }

        // The normal viewer's "Remote Control" button - creates a PENDING control session and
        // asks the host to Allow/Deny it, rather than forwarding to the host like the other
        // viewer message types below (there is no existing control session yet for the host to
        // route this against).
        if (msg.type === 'request-control') {
            const controlSession = store.createControlSessionForViewer(session.id);
            if (!controlSession) {
                sendError(socket, 'session-unavailable');
                return;
            }
            controlSession.requesterSocket = socket;
            logger.info('control-session.requested', { sessionId: session.id, controlSessionId: controlSession.id, viewerId });
            send(socket, {
                type: 'control-request-pending',
                controlSessionId: controlSession.id,
            });
            send(session.hostSocket, {
                type: 'control-request',
                controlSessionId: controlSession.id,
                viewerId,
            });
            return;
        }

        send(session.hostSocket, { ...msg, viewerId });
    });

    socket.on('close', () => {
        store.removeViewer(session, viewerId);
        logger.info('viewer.left', { sessionId: session.id, viewerId, viewerCount: session.viewers.size });
        send(session.hostSocket, { type: 'viewer-left', viewerId, viewerCount: session.viewers.size });
    });

    socket.on('error', (err) => logger.warn('viewer.socket-error', { sessionId: session.id, message: err.message }));
}

function handleControlViewer(socket, url, tracker) {
    const sessionId = url.searchParams.get('session');
    const pairingCode = url.searchParams.get('pairing');
    const session = sessionId ? store.get(sessionId) : null;

    if (!session) {
        sendError(socket, 'session-unavailable');
        socket.close(4004, 'session not found');
        return;
    }
    if (session.status !== SessionStatus.LIVE && session.status !== SessionStatus.STARTING) {
        sendError(socket, 'session-unavailable');
        socket.close(4004, 'session not live');
        return;
    }

    if (!pairingCode) {
        sendError(socket, 'missing-pairing-code');
        socket.close(4004, 'pairing code required');
        return;
    }

    const controlSession = store.getControlSessionByPairingCode(pairingCode);
    if (!controlSession || controlSession.screenSessionId !== sessionId) {
        sendError(socket, 'invalid-pairing-code');
        socket.close(4004, 'invalid pairing code');
        return;
    }

    if (controlSession.status === 'PENDING') {
        // Waiting for host authorization - store socket and wait
        controlSession.viewerSocket = socket;
        logger.info('control-viewer.waiting', { sessionId: session.id, controlSessionId: controlSession.id });
        send(socket, {
            type: 'control-waiting-authorization',
            controlSessionId: controlSession.id,
            pairingCode: controlSession.pairingCode,
        });
    } else if (controlSession.status === 'AUTHORIZED') {
        // Authorized but not yet activated - activate now
        const viewerId = 'ctrl-' + require('crypto').randomBytes(4).toString('hex');
        if (!store.activateControlSession(controlSession.id, socket, viewerId)) {
            sendError(socket, 'control-session-cannot-activate');
            socket.close(4004, 'cannot activate control session');
            return;
        }
        logger.info('control-viewer.activated', { sessionId: session.id, controlSessionId: controlSession.id, viewerId });
        send(socket, {
            type: 'control-authorized',
            controlSessionId: controlSession.id,
            viewerId,
            iceServers: buildIceServers(config, `control:${controlSession.id}`),
            screenSessionId: session.id,
            startedAt: session.startedAt,
            expiresAt: session.expiresAt,
            serverNow: Date.now(),
        });
        send(session.hostSocket, { type: 'control-viewer-joined', controlSessionId: controlSession.id, viewerId });
    } else if (controlSession.status === 'ACTIVE') {
        // Already active - reject new connection (one controller at a time)
        sendError(socket, 'control-session-already-active');
        socket.close(4004, 'control session already active');
        return;
    } else {
        // REVOKED, EXPIRED
        sendError(socket, 'control-session-' + controlSession.status.toLowerCase());
        socket.close(4004, 'control session ' + controlSession.status.toLowerCase());
        return;
    }

    socket.on('message', (raw) => {
        if (!tracker.allow()) {
            sendError(socket, 'rate-limited');
            return;
        }

        let msg;
        try {
            msg = JSON.parse(raw.toString());
        } catch {
            sendError(socket, 'malformed-json');
            return;
        }

        const validation = validateViewerMessage(msg);
        if (!validation.ok) {
            logger.warn('control-viewer.invalid-message', { reason: validation.reason, type: msg && msg.type });
            sendError(socket, 'invalid-message');
            return;
        }

        // Forward control signaling to host
        if (controlSession.status === 'ACTIVE') {
            send(session.hostSocket, { ...msg, controlSessionId: controlSession.id, controlToken: controlSession.controlToken });
        }
    });

    socket.on('close', () => {
        if (controlSession.status === 'ACTIVE') {
            store.deactivateControlSession(controlSession.id);
            send(session.hostSocket, { type: 'control-viewer-left', controlSessionId: controlSession.id });
            logger.info('control-viewer.left', { sessionId: session.id, controlSessionId: controlSession.id });
        } else if (controlSession.status === 'PENDING' || controlSession.status === 'AUTHORIZED') {
            controlSession.viewerSocket = null;
        }
    });

    socket.on('error', (err) => logger.warn('control-viewer.socket-error', { sessionId: session.id, message: err.message }));
}

/** Shared teardown for an explicit stop, a host disconnect, or a heartbeat timeout. */
function endSession(session, reason, logMessage) {
    store.beginStopping(session);
    notifyViewers(session, { type: 'session-ended', reason });
    notifyControlViewers(session, { type: 'session-ended', reason });
    closeAllViewers(session, 4001, logMessage);
    try {
        session.hostSocket.close(1000, logMessage);
    } catch {
        /* already closed */
    }
    store.markStopped(session);
    logger.info('session.stopped', { sessionId: session.id, reason });
}

const cleanupTimer = setInterval(() => {
    const events = store.runCleanupTick();
    for (const { session, reason } of events) {
        if (reason === 'expired') {
            logger.info('session.expired', { sessionId: session.id });
            notifyViewers(session, { type: 'session-expired' });
            notifyControlViewers(session, { type: 'session-expired' });
            send(session.hostSocket, { type: 'session-expired' });
            endSession(session, 'expired', 'session duration elapsed');
        } else if (reason === 'host-timeout') {
            logger.warn('session.host-timeout', { sessionId: session.id });
            notifyViewers(session, { type: 'session-ended', reason: 'host-timeout' });
            notifyControlViewers(session, { type: 'session-ended', reason: 'host-timeout' });
            closeAllViewers(session, 4001, 'host heartbeat timeout');
            store.beginStopping(session);
            store.markStopped(session);
        } else if (reason === 'host-disconnect-timeout') {
            logger.warn('session.host-disconnect-timeout', { sessionId: session.id });
            notifyViewers(session, { type: 'session-ended', reason: 'host-disconnected' });
            notifyControlViewers(session, { type: 'session-ended', reason: 'host-disconnected' });
            closeAllViewers(session, 4001, 'host did not reconnect in time');
            store.beginStopping(session);
            store.markStopped(session);
        }
    }
}, config.cleanupIntervalMs);

server.listen(config.port, config.bindHost, () => {
    if (config.isLanTest) {
        logger.info('server.listening', { 
            port: config.port, 
            env: config.env,
            bindHost: config.bindHost,
            lanIp: lanIp,
            signalingOrigin: signalingOrigin,
            viewerBaseUrl: viewerBaseUrl,
            note: 'LAN TEST MODE - accessible from other devices on the network'
        });
    } else {
        logger.info('server.listening', { port: config.port, env: config.env });
    }
});

/**
 * Graceful shutdown on SIGTERM/SIGINT - Render (and most PaaS/process supervisors) send SIGTERM
 * before killing a process for a redeploy or restart. Without this, in-flight sessions would
 * just vanish with no notification, leaving viewers stuck showing "Live" with a dead connection
 * until their own ICE/connection-state timeout eventually fires. Reuses the same
 * notify-then-close pattern as endSession rather than a separate teardown path.
 */
function shutdown(signal) {
    logger.info('server.shutting-down', { signal, activeSessions: store.activeSessionCount });
    clearInterval(cleanupTimer);
    for (const session of store.sessions.values()) {
        if (session.status === SessionStatus.STOPPED) continue;
        notifyViewers(session, { type: 'session-ended', reason: 'server-shutdown' });
        notifyControlViewers(session, { type: 'session-ended', reason: 'server-shutdown' });
        closeAllViewers(session, 1001, 'server shutting down');
        try {
            session.hostSocket.close(1001, 'server shutting down');
        } catch {
            /* already closed */
        }
    }
    server.close(() => {
        logger.info('server.shutdown-complete', {});
        process.exit(0);
    });
    // Belt-and-suspenders: don't hang forever if server.close()'s callback never fires (e.g. a
    // socket that won't close cleanly) - Render escalates to SIGKILL shortly after SIGTERM anyway.
    setTimeout(() => process.exit(0), 5000).unref();
}

process.on('SIGTERM', () => shutdown('SIGTERM'));
process.on('SIGINT', () => shutdown('SIGINT'));

/**
 * Defense-in-depth: a single unexpected exception (e.g. in a message handler this session's
 * testing didn't cover) must never take down the whole process and every other in-flight
 * session with it. Node's default behavior for an uncaught exception/unhandled rejection is to
 * crash the process - log it with full detail instead and keep serving existing connections.
 * This does not replace fixing the underlying bug if one is found via these logs; it bounds the
 * blast radius in the meantime. See docs/architecture/phase-3-technology-decision.md ("Render
 * reliability investigation") for why this was added defensively even though the flakiness
 * measured there was attributed to Render's free-tier cold start, not an application crash.
 */
process.on('uncaughtException', (err) => {
    logger.error('process.uncaught-exception', { message: err.message, stack: err.stack });
});
process.on('unhandledRejection', (reason) => {
    logger.error('process.unhandled-rejection', { reason: reason instanceof Error ? reason.message : String(reason) });
});