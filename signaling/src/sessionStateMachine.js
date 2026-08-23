'use strict';

/**
 * Session lifecycle states and the only transitions the server allows. Kept as an explicit
 * table (not just "set the field") so an invalid transition is a thrown error, not a silent
 * data-integrity bug - see docs/architecture/phase-2-technology-decision.md for why this needed
 * to be a real state machine once auto-expiration and host-disconnect cleanup both compete to
 * change a session's state.
 */
const SessionStatus = Object.freeze({
  CREATED: 'CREATED',
  STARTING: 'STARTING',
  LIVE: 'LIVE',
  STOPPING: 'STOPPING',
  EXPIRED: 'EXPIRED',
  STOPPED: 'STOPPED',
});

const ALLOWED_TRANSITIONS = {
  CREATED: ['STARTING', 'STOPPING'],
  STARTING: ['LIVE', 'STOPPING'],
  LIVE: ['STOPPING', 'EXPIRED'],
  EXPIRED: ['STOPPING'],
  STOPPING: ['STOPPED'],
  STOPPED: [],
};

class InvalidTransitionError extends Error {
  constructor(from, to) {
    super(`invalid session state transition: ${from} -> ${to}`);
    this.name = 'InvalidTransitionError';
    this.from = from;
    this.to = to;
  }
}

/** Throws InvalidTransitionError if the transition isn't allowed; otherwise returns `to`. */
function assertTransition(from, to) {
  const allowed = ALLOWED_TRANSITIONS[from];
  if (!allowed || !allowed.includes(to)) {
    throw new InvalidTransitionError(from, to);
  }
  return to;
}

/** Whether new viewers may join a session in this state. */
function isJoinable(status) {
  return status === SessionStatus.LIVE;
}

/** Whether the session is in some terminal-or-terminating state. */
function isEnding(status) {
  return status === SessionStatus.STOPPING || status === SessionStatus.STOPPED || status === SessionStatus.EXPIRED;
}

module.exports = { SessionStatus, assertTransition, isJoinable, isEnding, InvalidTransitionError };
