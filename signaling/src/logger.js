'use strict';

/**
 * Minimal structured logger. Signaling never sees media, so there's no risk of logging frame/
 * audio data here by construction - but session secrets (hostToken, TURN credentials) must
 * still never be logged, so callers pass plain, pre-sanitized fields only.
 */
const LEVELS = { error: 0, warn: 1, info: 2, debug: 3 };
const config = require('./config');
const currentLevel = LEVELS[config.logLevel] ?? LEVELS.info;

function log(level, event, fields = {}) {
  if (LEVELS[level] > currentLevel) return;
  const entry = { ts: new Date().toISOString(), level, event, ...fields };
  const line = JSON.stringify(entry);
  if (level === 'error') console.error(line);
  else if (level === 'warn') console.warn(line);
  else console.log(line);
}

module.exports = {
  error: (event, fields) => log('error', event, fields),
  warn: (event, fields) => log('warn', event, fields),
  info: (event, fields) => log('info', event, fields),
  debug: (event, fields) => log('debug', event, fields),
};
