'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { SessionStore } = require('../src/sessionStore');
const { SessionStatus } = require('../src/sessionStateMachine');

function fakeSocket() {
  return { readyState: 1, OPEN: 1, sent: [], send(m) { this.sent.push(m); }, close() {} };
}

function makeStore(overrides = {}) {
  return new SessionStore({ maxViewersPerSession: 2, hostHeartbeatTimeoutMs: 45000, ...overrides });
}

test('createSession assigns an unguessable id and a separate host token', () => {
  const store = makeStore();
  const a = store.createSession(fakeSocket(), 'oneHour');
  const b = store.createSession(fakeSocket(), 'oneHour');

  assert.notEqual(a.id, b.id);
  assert.notEqual(a.hostToken, b.hostToken);
  assert.notEqual(a.id, a.hostToken);
  assert.ok(a.id.length >= 16, 'session id should have real entropy, not be a short/sequential value');
  assert.equal(a.status, SessionStatus.CREATED);
});

test('session ids are not sequential', () => {
  const store = makeStore();
  const ids = new Set();
  for (let i = 0; i < 50; i++) {
    ids.add(store.createSession(fakeSocket(), 'oneHour').id);
  }
  assert.equal(ids.size, 50, 'all generated ids should be unique across many creations');
});

test('valid lifecycle: CREATED -> STARTING -> LIVE -> STOPPING -> STOPPED', () => {
  const store = makeStore();
  const session = store.createSession(fakeSocket(), 'fifteenMinutes');
  store.markStarting(session);
  assert.equal(session.status, SessionStatus.STARTING);
  store.markLive(session);
  assert.equal(session.status, SessionStatus.LIVE);
  assert.ok(session.startedAt);
  assert.ok(session.expiresAt > session.startedAt);
  store.beginStopping(session);
  assert.equal(session.status, SessionStatus.STOPPING);
  store.markStopped(session);
  assert.equal(session.status, SessionStatus.STOPPED);
});

test('untilStopped duration never computes an expiry', () => {
  const store = makeStore();
  const session = store.createSession(fakeSocket(), 'untilStopped');
  store.markStarting(session);
  store.markLive(session);
  assert.equal(session.expiresAt, null);
});

test('invalid transitions throw rather than silently changing state', () => {
  const store = makeStore();
  const session = store.createSession(fakeSocket(), 'oneHour');
  // CREATED -> LIVE directly is not allowed (must pass through STARTING).
  assert.throws(() => store.markLive(session));
  assert.equal(session.status, SessionStatus.CREATED, 'status must be unchanged after a rejected transition');
});

test('viewers can only join a LIVE session', () => {
  const store = makeStore();
  const session = store.createSession(fakeSocket(), 'oneHour');
  assert.throws(() => store.addViewer(session, fakeSocket()), /NOT_JOINABLE|not joinable/);

  store.markStarting(session);
  assert.throws(() => store.addViewer(session, fakeSocket()));

  store.markLive(session);
  const viewerId = store.addViewer(session, fakeSocket());
  assert.ok(viewerId);
  assert.equal(session.viewers.size, 1);
});

test('viewer capacity is enforced', () => {
  const store = makeStore({ maxViewersPerSession: 1 });
  const session = store.createSession(fakeSocket(), 'oneHour');
  store.markStarting(session);
  store.markLive(session);

  store.addViewer(session, fakeSocket());
  assert.throws(() => store.addViewer(session, fakeSocket()), (err) => err.code === 'CAPACITY');
});

test('authorizeHost rejects a wrong or missing token', () => {
  const store = makeStore();
  const session = store.createSession(fakeSocket(), 'oneHour');
  assert.equal(store.authorizeHost(session, 'not-the-real-token'), false);
  assert.equal(store.authorizeHost(session, session.hostToken), true);
});

test('cleanup tick expires a LIVE session once its expiresAt has passed', () => {
  const store = makeStore();
  const session = store.createSession(fakeSocket(), 'fifteenMinutes');
  store.markStarting(session);
  store.markLive(session);
  session.expiresAt = Date.now() - 1; // force past expiry without waiting 15 real minutes

  const events = store.runCleanupTick();

  assert.equal(events.length, 1);
  assert.equal(events[0].reason, 'expired');
  assert.equal(session.status, SessionStatus.EXPIRED);
});

test('cleanup tick flags a stale host heartbeat as host-timeout', () => {
  const store = makeStore({ maxViewersPerSession: 2, hostHeartbeatTimeoutMs: 1000 });
  const session = store.createSession(fakeSocket(), 'untilStopped');
  store.markStarting(session);
  session.lastHostHeartbeatAt = Date.now() - 5000;

  const events = store.runCleanupTick();

  assert.equal(events.length, 1);
  assert.equal(events[0].reason, 'host-timeout');
});

test('markHostDisconnected does not immediately end the session', () => {
  const store = makeStore();
  const session = store.createSession(fakeSocket(), 'untilStopped');
  store.markStarting(session);
  store.markLive(session);

  store.markHostDisconnected(session);

  assert.equal(session.status, SessionStatus.LIVE, 'status must not change on disconnect alone - only after the grace period elapses unresumed');
  assert.ok(session.hostDisconnectedAt, 'disconnect timestamp should be recorded');
});

test('resumeHost reattaches a new socket within the grace period and clears the disconnect flag', () => {
  const store = makeStore({ hostReconnectGraceMs: 20000 });
  const session = store.createSession(fakeSocket(), 'untilStopped');
  store.markStarting(session);
  store.markLive(session);
  store.markHostDisconnected(session);

  const newSocket = fakeSocket();
  const resumed = store.resumeHost(session, newSocket);

  assert.equal(resumed, true);
  assert.equal(session.hostSocket, newSocket);
  assert.equal(session.hostDisconnectedAt, null);
});

test('resumeHost refuses to reattach once the grace period has elapsed', () => {
  const store = makeStore({ hostReconnectGraceMs: 1000 });
  const session = store.createSession(fakeSocket(), 'untilStopped');
  store.markStarting(session);
  store.markLive(session);
  store.markHostDisconnected(session);
  session.hostDisconnectedAt = Date.now() - 5000; // force past the grace window

  const resumed = store.resumeHost(session, fakeSocket());

  assert.equal(resumed, false, 'a resume attempt after the grace period must be rejected');
});

test('cleanup tick ends a session whose host never resumed within the grace period', () => {
  const store = makeStore({ hostReconnectGraceMs: 1000 });
  const session = store.createSession(fakeSocket(), 'untilStopped');
  store.markStarting(session);
  store.markLive(session);
  store.markHostDisconnected(session);
  session.hostDisconnectedAt = Date.now() - 5000;

  const events = store.runCleanupTick();

  assert.equal(events.length, 1);
  assert.equal(events[0].reason, 'host-disconnect-timeout');
});

test('cleanup tick leaves a recently-disconnected session alone while still inside the grace period', () => {
  const store = makeStore({ hostReconnectGraceMs: 20000 });
  const session = store.createSession(fakeSocket(), 'untilStopped');
  store.markStarting(session);
  store.markLive(session);
  store.markHostDisconnected(session); // just now - well within a 20s grace period

  const events = store.runCleanupTick();

  assert.equal(events.length, 0, 'a session inside its reconnect grace period must not be torn down yet');
  assert.equal(session.status, SessionStatus.LIVE);
});

test('a STOPPED session is reaped from the store on the next cleanup tick', () => {
  const store = makeStore();
  const session = store.createSession(fakeSocket(), 'oneHour');
  store.markStarting(session);
  store.beginStopping(session);
  store.markStopped(session);

  store.runCleanupTick();

  assert.equal(store.get(session.id), undefined);
});
