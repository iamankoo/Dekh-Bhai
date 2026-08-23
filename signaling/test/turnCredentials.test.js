'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { generateTurnCredentials, buildIceServers } = require('../src/turnCredentials');

test('generateTurnCredentials returns null without a secret', () => {
  assert.equal(generateTurnCredentials({ secret: null, label: 'x', ttlSeconds: 60 }), null);
});

test('generateTurnCredentials produces a coturn-shaped username (expiry:label)', () => {
  const before = Math.floor(Date.now() / 1000);
  const cred = generateTurnCredentials({ secret: 'shh', label: 'session:abc', ttlSeconds: 3600 });
  const [expiryStr, ...labelParts] = cred.username.split(':');
  const expiry = parseInt(expiryStr, 10);

  assert.ok(expiry >= before + 3600 - 2 && expiry <= before + 3600 + 2, 'expiry should be ~ttlSeconds from now');
  assert.equal(labelParts.join(':'), 'session:abc');
  assert.ok(cred.password.length > 0);
});

test('generateTurnCredentials is deterministic for the same inputs at the same second', () => {
  const a = generateTurnCredentials({ secret: 'shh', label: 'x', ttlSeconds: 60 });
  const b = generateTurnCredentials({ secret: 'shh', label: 'x', ttlSeconds: 60 });
  // usernames may differ by a second's jitter across two calls; passwords are a pure function
  // of (secret, username), so recomputing the password for a's own username must match exactly.
  const crypto = require('node:crypto');
  const expectedPassword = crypto.createHmac('sha1', 'shh').update(a.username).digest('base64');
  assert.equal(a.password, expectedPassword);
  assert.notEqual(a.password, crypto.createHmac('sha1', 'different-secret').update(a.username).digest('base64'));
});

test('buildIceServers returns STUN-only when no TURN secret is configured', () => {
  const servers = buildIceServers({ stunUrls: ['stun:stun.example.com'], turnUrl: null, turnSecret: null }, 'x');
  assert.equal(servers.length, 1);
  assert.equal(servers[0].urls, 'stun:stun.example.com');
});

test('buildIceServers appends a fresh TURN credential when configured', () => {
  const servers = buildIceServers(
    { stunUrls: ['stun:stun.example.com'], turnUrl: 'turn:turn.example.com:3478', turnSecret: 'shh', turnCredentialTtlSeconds: 3600 },
    'session:xyz'
  );
  assert.equal(servers.length, 2);
  const turnServer = servers[1];
  assert.equal(turnServer.urls, 'turn:turn.example.com:3478');
  assert.ok(turnServer.username.endsWith(':session:xyz'));
  assert.ok(turnServer.credential.length > 0);
});
