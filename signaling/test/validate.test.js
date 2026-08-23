'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { validateHostMessage, validateViewerMessage } = require('../src/validate');

test('validateHostMessage accepts a well-formed create-session', () => {
  const result = validateHostMessage({ type: 'create-session', duration: 'oneHour' });
  assert.equal(result.ok, true);
});

test('validateHostMessage rejects an invalid duration', () => {
  const result = validateHostMessage({ type: 'create-session', duration: 'twoHours' });
  assert.equal(result.ok, false);
});

test('validateHostMessage rejects offer without hostToken', () => {
  const result = validateHostMessage({ type: 'offer', viewerId: 'v1', sdp: 'v=0...' });
  assert.equal(result.ok, false);
});

test('validateHostMessage rejects an unknown message type', () => {
  const result = validateHostMessage({ type: 'delete-everything' });
  assert.equal(result.ok, false);
});

test('validateHostMessage rejects non-object input safely (no throw)', () => {
  assert.doesNotThrow(() => validateHostMessage(null));
  assert.doesNotThrow(() => validateHostMessage('not an object'));
  assert.doesNotThrow(() => validateHostMessage(42));
  assert.equal(validateHostMessage(null).ok, false);
});

test('validateViewerMessage accepts a well-formed answer', () => {
  const result = validateViewerMessage({ type: 'answer', sdp: 'v=0...' });
  assert.equal(result.ok, true);
});

test('validateViewerMessage rejects a viewer trying to send a host-only message', () => {
  const result = validateViewerMessage({ type: 'create-session', duration: 'oneHour' });
  assert.equal(result.ok, false);
});

test('validateViewerMessage rejects an oversized payload', () => {
  const result = validateViewerMessage({ type: 'answer', sdp: 'x'.repeat(100000) });
  assert.equal(result.ok, false);
});
