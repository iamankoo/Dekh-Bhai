'use strict';

const crypto = require('crypto');

/**
 * Generates short-lived TURN credentials using coturn's standard "REST API" / long-term-
 * credential-mechanism scheme (the same one coturn's `use-auth-secret` option implements):
 * username = "<expiryEpochSeconds>:<label>", password = base64(HMAC-SHA1(secret, username)).
 * coturn derives the real per-session key from this at connect time - the shared secret itself
 * never goes to a client, only these time-limited derived credentials do, and only for as long
 * as `ttlSeconds` says. See docs/deployment/phase-2.md for the coturn-side configuration.
 */
function generateTurnCredentials({ secret, label, ttlSeconds }) {
  if (!secret) return null;

  const expiry = Math.floor(Date.now() / 1000) + ttlSeconds;
  const username = `${expiry}:${label}`;
  const password = crypto.createHmac('sha1', secret).update(username).digest('base64');

  return { username, password, ttlSeconds };
}

/**
 * Builds the RTCIceServer list for a session: the configured STUN server(s) plus, if a TURN
 * shared secret is configured, a freshly minted TURN credential. Falls back to STUN-only when
 * no TURN secret is configured (e.g. local development) rather than failing.
 */
function buildIceServers(config, label) {
  const servers = [...config.stunUrls.map((urls) => ({ urls }))];

  if (config.turnUrl && config.turnSecret) {
    const cred = generateTurnCredentials({
      secret: config.turnSecret,
      label,
      ttlSeconds: config.turnCredentialTtlSeconds,
    });
    servers.push({
      urls: config.turnUrl,
      username: cred.username,
      credential: cred.password,
    });
  }

  return servers;
}

module.exports = { generateTurnCredentials, buildIceServers };
