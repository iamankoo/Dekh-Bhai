'use strict';

require('dotenv').config(); // no-op in production if no .env file is present - real env vars win

/**
 * All environment-driven configuration in one place. Nothing in this file is a secret value -
 * it's the list of variable NAMES and their safe development defaults. Actual secrets (a real
 * TURN_SECRET) are only ever supplied via the environment at deploy time - see
 * docs/deployment/phase-2.md and .env.example.
 */

function parseIntEnv(name, fallback) {
  const raw = process.env[name];
  if (!raw) return fallback;
  const n = parseInt(raw, 10);
  return Number.isFinite(n) ? n : fallback;
}

function parseBoolEnv(name, fallback) {
  const raw = process.env[name];
  if (!raw) return fallback;
  return raw === '1' || raw.toLowerCase() === 'true';
}

const isLanTest = parseBoolEnv('LAN_TEST', false);
const env = process.env.NODE_ENV || 'development';
// Render (and most PaaS hosts) probe the container's external port on 0.0.0.0 specifically - a
// process bound only to 127.0.0.1 logs "listening" successfully but is completely unreachable
// from outside the container, which Render reports as "No open ports detected on 0.0.0.0" and
// never routes traffic to. LAN_TEST already needed 0.0.0.0 for other devices on the network to
// reach it; production needs exactly the same thing for Render's own routing to reach it, so
// loopback-only stays the default for plain local dev only.
const bindHost = isLanTest || env === 'production' ? '0.0.0.0' : '127.0.0.1';

const config = {
  env,
  port: parseIntEnv('PORT', 8787),
  bindHost,
  isLanTest,

  // ICE / TURN
  stunUrls: (process.env.STUN_URLS || 'stun:stun.l.google.com:19302')
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean),
  turnUrl: process.env.TURN_URL || null,
  turnSecret: process.env.TURN_SECRET || null,
  turnCredentialTtlSeconds: parseIntEnv('TURN_CREDENTIAL_TTL_SECONDS', 3600),

  // Session policy
  maxViewersPerSession: parseIntEnv('MAX_VIEWERS_PER_SESSION', 25),
  hostHeartbeatIntervalMs: parseIntEnv('HOST_HEARTBEAT_INTERVAL_MS', 15000),
  hostHeartbeatTimeoutMs: parseIntEnv('HOST_HEARTBEAT_TIMEOUT_MS', 45000),
  cleanupIntervalMs: parseIntEnv('CLEANUP_INTERVAL_MS', 10000),

  // How long a session survives its host's TCP connection dropping before being torn down, to
  // give a genuine network blip (or a Render free-tier idle-wake hiccup) a chance to reconnect
  // and resume rather than instantly ending the session and kicking every viewer - see
  // docs/architecture/phase-3-technology-decision.md ("Host reconnect / resume-session").
  hostReconnectGraceMs: parseIntEnv('HOST_RECONNECT_GRACE_MS', 20000),

  // Basic per-connection rate limiting (messages per window)
  rateLimitWindowMs: parseIntEnv('RATE_LIMIT_WINDOW_MS', 1000),
  rateLimitMaxMessages: parseIntEnv('RATE_LIMIT_MAX_MESSAGES', 50),

  logLevel: process.env.LOG_LEVEL || 'info',

  // Test-only: shortens every fixed duration to this many ms so an end-to-end expiration test
  // doesn't have to wait out a real 15-minute/1-hour/5-hour session. Never set in production.
  testDurationOverrideMs: process.env.TEST_DURATION_OVERRIDE_MS ? parseIntEnv('TEST_DURATION_OVERRIDE_MS', 0) : null,
};

module.exports = config;
