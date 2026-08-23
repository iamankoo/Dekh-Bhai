# Phase 2 Technology Decision

This document records what changed for Phase 2 (turning the Phase 1 local casting engine into
an Internet-based mirroring product), why each piece was built the way it was, rejected
alternatives, and known limitations. It assumes `docs/architecture/phase-1-technology-decision.md`
as background - Phase 2 deliberately did not touch the proven capture/audio/encode/WebRTC engine
described there.

## Scope of Phase 2

Per the Phase 2 brief: turn the proven local pipeline into a real product with public session
links, a proper session lifecycle (four durations, server-enforced expiration), QR codes,
production-capable signaling (WSS, STUN/TURN), a redesigned minimal host UI and viewer UX,
reconnection behavior, and basic anonymous-session security. Explicitly out of scope: accounts,
recording/storage, meeting-style features, and a rewrite of the Phase 1 media pipeline.

## Session model and state machine

**Decision**: an explicit, frozen state table (`signaling/src/sessionStateMachine.js`) -
`CREATED -> STARTING -> LIVE -> {STOPPING | EXPIRED} -> STOPPING -> STOPPED` - enforced by
`assertTransition`, which throws `InvalidTransitionError` rather than letting a field get
silently overwritten.

**Why**: once auto-expiration (a timer-driven tick) and host-disconnect/heartbeat-timeout cleanup
(also timer-driven) both compete to change the same session's status, "just set `session.status`"
stops being safe - two code paths can race to move a session out of `LIVE`. An explicit transition
table turns a would-be race condition into a thrown, loud error during development instead of a
silent data-integrity bug in production. The desktop side mirrors this conceptually in
`SessionController` (`desktop/src/DekhBhai.Core/Session/SessionController.cs`), but the **server
is authoritative** - the host never assumes a fixed-duration session is still valid just because
its own local timer hasn't fired; it reacts to `session-expired`/`session-ended` pushed from the
server.

**Rejected**: a plain mutable `status` string with no transition guard - the original approach
before expiration/heartbeat-timeout cleanup were added; abandoned once two independent triggers
for "session ends" existed, for the race-condition reason above.

## Session storage: in-memory, not a database

**Decision**: `SessionStore` (`signaling/src/sessionStore.js`) is a plain `Map` living in the
signaling process's memory. No database was introduced.

**Why**: Dekh Bhai never stores media, and a session record itself is worthless once the session
ends (there is nothing to look up later - see "No media storage" in the master brief). Phase 2's
deployment target is a single signaling process; restarting that process ending in-flight
sessions is an acceptable, explicit limitation for this phase, not an oversight. Introducing a
database (even an embedded one like SQLite) would add an operational dependency (migrations,
connection handling, backup) with no corresponding product benefit at this scale.

**Future option, not built**: if Phase 3 needs multiple signaling instances behind a load
balancer, session state would need to move to a shared store (Redis is the natural choice - fast,
TTL-native, no schema) so any instance can serve any session. This is a scaling concern, not a
Phase 2 requirement, and was deliberately not built ahead of need.

## Secure session identifiers

**Decision**: `crypto.randomBytes(16).toString('base64url')` for the public session id (~22
unguessable characters) and a **separate** `crypto.randomBytes(24)` host token
(`sessionStore.js: generateSessionId`/`generateHostToken`). The host token is never sent to
viewers and is required on every host-authenticated signaling message
(`server.js`: `store.authorizeHost`).

**Why**: the brief requires session URLs that are difficult to guess and don't leak internal
IDs. A 16-byte random id has no practical brute-force surface, is never sequential, and doubles
as an opaque `Map` key (no path-traversal or SQL-injection surface, since it's never used as a
filesystem path or interpolated into a query). Splitting the host token from the session id means
knowing a *viewer* link (which is meant to be shared) grants zero ability to send host-privileged
messages (`stop-session`, `heartbeat`, offers) on that session - verified by
`signaling/test/sessionStore.test.js: "authorizeHost rejects a wrong or missing token"`.

**Rejected**: sequential/incrementing ids (explicitly disqualified by the brief) and reusing the
host token as the shareable id (would let anyone with the viewer link impersonate the host).

## Production signaling protocol

**Decision**: kept the existing Node.js (`ws` + `express`) WebSocket relay from Phase 1 and
extended its message set rather than rewriting it - `create-session`, `host-live`, `heartbeat`,
`stop-session`, `offer`/`answer`, `ice-candidate` (see `signaling/src/server.js` and
`signaling/src/validate.js` for the authoritative per-type shape). Every inbound message is
validated before being acted on (`validateHostMessage`/`validateViewerMessage`); malformed or
unrecognized messages are rejected with an `error` reply, never a crash.

**Why**: the Phase 1 relay already only ever carried SDP/ICE/session-lifecycle JSON, never media -
exactly the Phase 2 requirement ("signaling server is NOT the media server"). Media stays on the
direct `Host <-> WebRTC <-> Viewer` path (`WebRtcHost.cs`); the Node process never touches a video
or audio byte. Extending the existing protocol (rather than replacing it) meant the proven
offer/answer/ICE relay logic didn't need to be re-validated from scratch.

**Production viewer URL shape**: `GET /v/:sessionId` (path-based, `server.js`) rather than the
Phase 1 `?session=<id>` query string, per the Phase 2 brief's example
(`https://dekhbhai.example/v/Ab7kQ92m`). `viewer.js: getSessionId()` reads the path first and
falls back to the query string, so old-style links (from a Phase 1 build) still resolve.

## STUN/TURN and ICE server delivery

**Decision**: STUN servers are configured server-side (`STUN_URLS`, default
`stun:stun.l.google.com:19302`) and TURN credentials are minted per-connection using coturn's
standard "REST API" / long-term-credential scheme
(`signaling/src/turnCredentials.js: generateTurnCredentials`): `username =
"<expiryEpochSeconds>:<label>"`, `password = base64(HMAC-SHA1(secret, username))`. The signaling
server sends the resulting `iceServers` array to both the host (in `session-created`) and each
viewer (in `joined`) - see `buildIceServers`. If no `TURN_SECRET` is configured, the array is
STUN-only (development default) rather than failing.

**Why**: STUN alone cannot traverse symmetric NATs, which the Phase 2 brief explicitly calls out
as a real-network condition that must be handled - a TURN relay is required for a genuine
"different networks" connection guarantee. The REST API scheme means the TURN **shared secret**
never leaves the server; only short-lived, per-connection derived credentials
(`turnCredentialTtlSeconds`, default 1 hour) are ever sent to a client, so a leaked client-side
credential expires quickly and can't be used to authenticate as a different session. This is the
same mechanism coturn's own `use-auth-secret` option implements, so no custom TURN server code was
needed - a stock coturn deployment with `use-auth-secret` + a shared `static-auth-secret` is a
drop-in fit (see `docs/deployment/phase-2.md`).

**Rejected**: static long-lived TURN username/password pairs baked into the app - would be a
permanent shared secret shipped to every client (violates "TURN credentials must NOT be
hard-coded" and "no exposed server credentials"). Rejected outright.

**Status**: the credential-minting code is implemented and unit-tested
(`signaling/test/turnCredentials.test.js` - 3 tests covering the null-secret fallback, the
coturn-shaped username, and HMAC determinism), but **no coturn instance has actually been deployed
and exercised end-to-end** in this phase - see "What has not been verified" below. The code is
ready to point at a real coturn deployment via `TURN_URL`/`TURN_SECRET`; standing that server up
and running a real different-network connectivity test is an infrastructure step, not a code
change.

## Rate limiting and message validation

**Decision**: a simple fixed-window per-connection limiter (`signaling/src/rateLimiter.js`,
default 50 messages/second) applied to every inbound WebSocket message, plus strict per-message-
type shape validation (`validate.js`) with bounded string lengths (64 KiB) before any message is
acted on.

**Why**: the signaling server is a small, single-purpose relay, not a general API gateway - a
fixed in-memory window is sufficient to stop one misbehaving/malicious connection from consuming
disproportionate CPU, and doesn't need the complexity of a sliding-window or distributed limiter
for a single-process deployment target. Shape validation exists so a malformed message is always
a rejected message (`sendError(socket, 'invalid-message')`), never a path to a crash or an
unhandled exception leaking a stack trace to the client.

## Host session lifecycle and error translation

**Decision**: `SessionController` (desktop) treats the server as authoritative for session
timing/expiration and never trusts its own clock for anything shown to the user - `startedAt`/
`expiresAt` come from the server's `live-ack` message and are used verbatim
(`SignalingClient.LiveAck`), and the elapsed-time display is recomputed from that server-issued
`startedAt` every UI tick rather than incremented locally (`MainWindow.xaml.cs:
UpdateElapsedText` - "so the displayed time cannot drift from UI thread jitter or a missed tick").
Low-level exception text is translated to an actionable message before being shown
(`SessionController.TranslateError`), e.g. a connection-refused/timeout exception becomes "Unable
to connect to Dekh Bhai's signaling service. Check your Internet connection and try again."
instead of a raw `.NET` exception message.

**Why**: the brief requires the visible timer to represent real elapsed session time (not a UI
countdown that can drift), and requires host errors to be understandable rather than raw
exceptions/stack traces.

## Deployment split: viewer on Vercel, signaling stays a long-lived process

**Decision**: deploy `viewer/` as a standalone static site (Vercel) while keeping the signaling
server (`signaling/`) as the same unmodified long-lived Node.js process from earlier in this
document, running on a normal host (a VM or a PaaS that runs persistent processes - Fly.io,
Render, Railway). See `docs/deployment/phase-2.md` for the deployment procedure.

**Why not deploy signaling to Vercel too**: Vercel added native WebSocket support in public beta
(June 2026), so it was investigated directly rather than dismissed from memory. Two of its
constraints rule it out for this signaling server as designed:

1. **Duration cap.** A Vercel Function's WebSocket connection closes when the function reaches
   its `maxDuration` - 60s/300s (Hobby, with Fluid Compute) up to 800s standard or 1800s in beta
   (Pro/Enterprise, specific runtimes only). The product's shortest offered session is **15
   minutes**; the others are **1 hour**, **5 hours**, and **Until I Stop (unbounded)**. None of
   these fit even the most generous 1800-second (30-minute) beta cap - a signaling connection
   would be forcibly dropped multiple times over the course of a normal session.
2. **No shared memory across function instances.** A reconnect is not guaranteed to land on the
   same Vercel Function instance, and Vercel's own guidance is that cross-instance state/routing
   needs an external store (Redis) plus a pub/sub relay layer built by the application. The
   current `sessionStore.js` design holds live socket references directly in an in-process `Map`
   (`session.hostSocket`, `viewer.socket`) and relays messages by calling `.send()` on those
   references directly - this is incompatible with "the host and a given viewer might be pinned
   to two different function instances" without a real rearchitecture (Redis-backed session state
   + Redis Pub/Sub message relay + a reconnect-and-reattach protocol on both the desktop
   `SignalingClient` and `viewer.js` to survive a forced disconnect every 13-30 minutes without
   ending the session).

Given the explicit brief instruction not to rewrite the working signaling architecture without a
demonstrated technical reason, and that the rework required would be substantial (new external
dependency, new failure modes, and sessions would still need to survive periodic forced
reconnects even at the *best* case duration cap available), signaling stays on a traditional
persistent host. The viewer has no such constraint - it's a static page with no server-held state
of its own, so it's an unconditionally good fit for Vercel.

**What changed in the viewer to support this split**: it previously assumed it was served from
the same origin as signaling (`wsUrl()` derived the WebSocket URL from
`window.location.host`). `viewer/config.js` (new) now holds a single overridable global,
`window.DEKHBHAI_SIGNALING_ORIGIN`, defaulting to empty (same-origin, i.e. unchanged local-dev
behavior) and settable to signaling's real origin for a split deployment. This mirrors the
existing `DEKHBHAI_VIEWER_BASE_URL`/`DEKHBHAI_SIGNALING_WS_URL` pattern already used on the
desktop side - public configuration, not a secret, kept out of the URL-derivation logic itself.

## What was deliberately not rebuilt

Per the brief's explicit "do not rewrite proven Phase 1 work" instruction, Phase 2 did **not**
touch: `WindowsGraphicsScreenCapture`, the VP8/Opus encoder pipelines, `WebRtcHost`'s one-
`RTCPeerConnection`-per-viewer model (already matches the Phase 2 "one capture, multiple peer
connections" requirement with no change needed), the WGC border-suppression mechanism, or the
MSIX packaging pipeline. Phase 2 code only adds the session/signaling/UI layers around that
engine.

## Known limitations (Phase 2)

- **No coturn instance has been deployed or tested against.** TURN credential minting is
  implemented and unit-tested in isolation; a real different-network WebRTC connection through a
  live TURN relay has not been exercised. See `docs/deployment/phase-2.md` for the deployment
  procedure and `docs/testing/test-plan.md` for the still-open test item.
- **No production domain/HTTPS/WSS endpoint exists.** The signaling server and viewer have not
  been deployed anywhere public; every test performed so far has been against
  `localhost`/development configuration. `DEKHBHAI_SIGNALING_WS_URL`/`DEKHBHAI_VIEWER_BASE_URL`
  (desktop) and `STUN_URLS`/`TURN_URL`/`TURN_SECRET` (signaling) are the seams that make pointing
  at a real deployment a configuration change, not a code change - but that deployment itself has
  not happened.
- **No cross-network, mobile-browser (Android/iOS), or multi-physical-machine test has been
  performed.** These require a live public deployment, a second network, and physical mobile
  devices, none of which are available in this development environment. Do not treat "the code
  implements multi-viewer/TURN/mobile-compatible APIs" as equivalent to "this was tested working
  on a phone over the Internet" - it has not been.
- **In-memory session store does not survive a signaling process restart.** In-flight sessions
  are lost if the process crashes or is redeployed - an explicit, accepted Phase 2 limitation
  (see "Session storage" above), not a bug.
- **Single signaling process, no horizontal scaling.** Fine for Phase 2's anonymous-casting scale;
  a shared session store (e.g. Redis) would be required before running more than one signaling
  instance - not built, since nothing in Phase 2 requires it yet.
- **`maxViewersPerSession` (default 25) and multi-viewer fan-out are implemented and unit-tested
  at the signaling layer** (`sessionStore.test.js: "viewer capacity is enforced"`), but the
  practical viewer limit under real load (CPU cost of N simultaneous `RTCPeerConnection`s
  encoding/sending to N viewers from one capture pipeline) has not been load-tested. Treat "3+
  concurrent viewers" as implemented-but-unmeasured, not benchmarked.
