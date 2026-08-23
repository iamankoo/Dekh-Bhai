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
app.use(express.static(viewerDir));

// Production viewer URLs are path-based (https://<domain>/v/<sessionId>) rather than a query
// string, per the Phase 2 brief - serve the same static viewer page there; viewer.js reads the
// session id back out of the path.
app.get('/v/:sessionId', (_req, res) => res.sendFile(path.join(viewerDir, 'index.html')));

app.get('/healthz', (_req, res) => res.json({ ok: true })); // kept for Phase 1 compatibility
app.get('/health', (_req, res) =>
  res.json({ status: 'ok', uptimeSeconds: Math.floor(process.uptime()), activeSessions: store.activeSessionCount })
);
app.get('/ready', (_req, res) => res.json({ ready: true }));

const server = http.createServer(app);
const wss = new WebSocketServer({ server, path: '/ws' });
const store = new SessionStore(config);
const rateLimiter = new RateLimiter({ windowMs: config.rateLimitWindowMs, maxMessages: config.rateLimitMaxMessages });

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

wss.on('connection', (socket, request) => {
  const url = new URL(request.url, `http://${request.headers.host}`);
  const role = url.searchParams.get('role');
  const tracker = rateLimiter.createTracker();

  if (role === 'host') {
    handleHost(socket, tracker);
  } else if (role === 'viewer') {
    handleViewer(socket, url, tracker);
  } else {
    socket.close(4000, 'role query param must be "host" or "viewer"');
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
    }
  });

  socket.on('close', () => {
    if (session && session.status !== SessionStatus.STOPPED) {
      logger.info('session.host-disconnected', { sessionId: session.id, status: session.status });
      endSession(session, 'host-disconnected', 'host connection closed');
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

    send(session.hostSocket, { ...msg, viewerId });
  });

  socket.on('close', () => {
    store.removeViewer(session, viewerId);
    logger.info('viewer.left', { sessionId: session.id, viewerId, viewerCount: session.viewers.size });
    send(session.hostSocket, { type: 'viewer-left', viewerId, viewerCount: session.viewers.size });
  });

  socket.on('error', (err) => logger.warn('viewer.socket-error', { sessionId: session.id, message: err.message }));
}

/** Shared teardown for an explicit stop, a host disconnect, or a heartbeat timeout. */
function endSession(session, reason, logMessage) {
  store.beginStopping(session);
  notifyViewers(session, { type: 'session-ended', reason });
  closeAllViewers(session, 4001, logMessage);
  try {
    session.hostSocket.close(1000, logMessage);
  } catch {
    /* already closed */
  }
  store.markStopped(session);
  logger.info('session.stopped', { sessionId: session.id, reason });
}

setInterval(() => {
  const events = store.runCleanupTick();
  for (const { session, reason } of events) {
    if (reason === 'expired') {
      logger.info('session.expired', { sessionId: session.id });
      notifyViewers(session, { type: 'session-expired' });
      send(session.hostSocket, { type: 'session-expired' });
      endSession(session, 'expired', 'session duration elapsed');
    } else if (reason === 'host-timeout') {
      logger.warn('session.host-timeout', { sessionId: session.id });
      notifyViewers(session, { type: 'session-ended', reason: 'host-timeout' });
      closeAllViewers(session, 4001, 'host heartbeat timeout');
      store.beginStopping(session);
      store.markStopped(session);
    }
  }
}, config.cleanupIntervalMs);

server.listen(config.port, () => {
  logger.info('server.listening', { port: config.port, env: config.env });
});
