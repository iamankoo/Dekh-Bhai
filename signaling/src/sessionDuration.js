'use strict';

/**
 * The four durations the product offers, plus the "no automatic expiration" case. Kept as one
 * table (id -> ms) rather than scattering magic numbers through session logic, and shared by
 * every place that needs to compute or validate expiresAt.
 */
const SESSION_DURATIONS = Object.freeze({
  fifteenMinutes: 15 * 60 * 1000,
  oneHour: 60 * 60 * 1000,
  fiveHours: 5 * 60 * 60 * 1000,
  untilStopped: null,
});

function isValidDuration(id) {
  return Object.prototype.hasOwnProperty.call(SESSION_DURATIONS, id);
}

/**
 * Returns an epoch-ms expiry, or null for untilStopped (never auto-expires).
 * @param {number} [overrideMs] Test-only: when set (via TEST_DURATION_OVERRIDE_MS - see
 *   config.js), replaces the real duration so an end-to-end expiration test doesn't have to
 *   wait out a real 15-minute/1-hour/5-hour session. Never set in production. untilStopped is
 *   deliberately NOT affected - it must keep meaning "no automatic expiration" even under test.
 */
function computeExpiresAt(durationId, fromEpochMs, overrideMs) {
  if (durationId === 'untilStopped') return null;
  const ms = overrideMs ?? SESSION_DURATIONS[durationId];
  return fromEpochMs + ms;
}

module.exports = { SESSION_DURATIONS, isValidDuration, computeExpiresAt };
