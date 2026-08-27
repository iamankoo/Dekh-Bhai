# Phase 3 Cross-Network Test

## Status: BLOCKED — physical second network/device unavailable, and TURN infrastructure unavailable

This development environment is a single Windows machine on a single network, with no second
physical device, no second network connection, and no cloud/hosting account to stand up a coturn
TURN server. The three tests the Phase 3 brief specifies (Test A: host Wi-Fi / viewer different
Wi-Fi; Test B: host Wi-Fi / viewer mobile data; Test C: host one network / a second physical
device on another network) **cannot be performed from here**, and are not claimed as passing.

This is not a smaller version of "cross-network" being reported as if it were the real thing -
none of what follows substitutes for an actual different-network connection. It documents what
*was* verified, so the actual gap is precise rather than vague.

## What was actually tested (same-network only)

All of this ran on one machine/network, verified with `RTCPeerConnection.getStats()` evidence -
see `docs/testing/phase-3-release-test.md` for the full detail:

- Installed MSIX host ↔ production Render signaling ↔ production Vercel viewer, same machine/
  network: video and audio confirmed flowing (`framesDecoded` growing, `framesDropped: 0`, audio
  samples growing).
- Two simultaneous viewers, same session, same network: both independently confirmed connected
  and decoding frames.
- Host signaling reconnect after a real (if same-network) TCP connection interruption: confirmed
  the session survives a short outage and correctly gives up after a long one.
- Earlier (Phase 2, same environment): an Android phone on the same Wi-Fi as the host successfully
  viewed a live session (video decoding at a real, if low, framerate) - done by the project owner
  directly, not by this agent, and still same-network.

None of these exercise NAT traversal, TURN relay, or genuinely different public IP paths - a
same-network WebRTC connection almost always succeeds via host or server-reflexive (STUN) ICE
candidates without ever needing a TURN relay, so same-network success provides **no evidence**
about cross-network reliability.

## TURN status: BLOCKED — TURN infrastructure unavailable

- **Code**: unchanged and already complete since Phase 2 - `signaling/src/turnCredentials.js`
  implements coturn's standard `use-auth-secret` REST credential scheme correctly (unit-tested:
  `turnCredentials.test.js`, 3 tests, all passing), and `buildIceServers` falls back to STUN-only
  when no `TURN_URL`/`TURN_SECRET` is configured.
- **Infrastructure**: no coturn instance has been deployed anywhere. This requires a VM or
  container host with a public IP, an open UDP relay port range, and a TLS certificate for the
  TURN realm - see `docs/deployment/phase-3.md` for the exact deployment procedure, ready to run
  once such infrastructure access exists.
- **Consequence**: any host or viewer behind a symmetric NAT (common on cellular networks and
  many corporate/CGNAT networks) will currently fail to connect to a peer on a different network,
  with no fallback. This is the single biggest gap between "works when I tried it" and "works as
  an Internet product" for Dekh Bhai today.

## What would need to happen to actually pass this test

1. Deploy coturn (procedure documented, not yet executed - `docs/deployment/phase-3.md`).
2. Set `TURN_URL`/`TURN_SECRET` on the Render signaling deployment (currently unset - confirmed
   via `render.yaml`, which declares them `sync: false` specifically for this future step).
3. Get a genuinely separate network path - a second physical device on cellular data is the
   simplest real test, since it's almost certain to be a different network/NAT than the host's
   Wi-Fi.
4. Re-run the same `getStats()` verification as the release test, and additionally record the
   **selected ICE candidate pair type** (`relay` proves TURN was actually used and needed;
   `srflx`/`host` would mean the connection succeeded without TURN even across networks, which is
   also useful information, not a failure).
5. Update this document with the actual result - PASS/FAIL per test, with the statistics listed
   in the Phase 3 brief (`connectionState`, `iceConnectionState`, `framesReceived`,
   `framesDecoded`, `framesDropped`, `frameWidth`/`frameHeight`, audio packets/samples) - not a
   summary claim.

## Explicit non-claims

Per the Phase 3 brief's testing standard: this document does **not** claim cross-network sharing
works, does **not** claim TURN works, and does **not** claim NAT traversal has been verified in
any form beyond same-network STUN/host candidates. Any statement elsewhere in this repository
that could be read as claiming otherwise should be corrected to point back here.
