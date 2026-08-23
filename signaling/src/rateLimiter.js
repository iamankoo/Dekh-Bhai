'use strict';

/**
 * Fixed-window per-connection message rate limiter. Deliberately simple (not sliding-window,
 * not distributed) - this is a single-process signaling server whose only job is to bound how
 * fast one WebSocket can make it do work, not a general API gateway.
 */
class RateLimiter {
  constructor({ windowMs, maxMessages }) {
    this.windowMs = windowMs;
    this.maxMessages = maxMessages;
  }

  /** Returns a per-connection tracker to call `allow()` on for each inbound message. */
  createTracker() {
    let windowStart = Date.now();
    let count = 0;
    return {
      allow: () => {
        const now = Date.now();
        if (now - windowStart >= this.windowMs) {
          windowStart = now;
          count = 0;
        }
        count += 1;
        return count <= this.maxMessages;
      },
    };
  }
}

module.exports = { RateLimiter };
