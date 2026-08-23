'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { computeExpiresAt, isValidDuration } = require('../src/sessionDuration');

test('computeExpiresAt uses the real duration table by default', () => {
  const from = 1000000;
  assert.equal(computeExpiresAt('fifteenMinutes', from), from + 15 * 60 * 1000);
  assert.equal(computeExpiresAt('oneHour', from), from + 60 * 60 * 1000);
});

test('computeExpiresAt returns null for untilStopped regardless of override', () => {
  assert.equal(computeExpiresAt('untilStopped', 1000000), null);
  assert.equal(computeExpiresAt('untilStopped', 1000000, 5000), null);
});

test('computeExpiresAt honors a test override for fixed durations only', () => {
  const from = 1000000;
  assert.equal(computeExpiresAt('fifteenMinutes', from, 5000), from + 5000);
  assert.equal(computeExpiresAt('untilStopped', from, 5000), null);
});

test('isValidDuration rejects anything not in the table', () => {
  assert.equal(isValidDuration('oneHour'), true);
  assert.equal(isValidDuration('twoHours'), false);
  assert.equal(isValidDuration(''), false);
  assert.equal(isValidDuration(undefined), false);
});
