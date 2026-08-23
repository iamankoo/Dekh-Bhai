# Test Plan

This document covers both Phase 1 (the local casting engine) and Phase 2 (session model,
production signaling, TURN, QR/duration UI, viewer UX). Neither phase has a fully automated
end-to-end test (that would require driving a live Windows desktop, a real browser, and - for
Phase 2 - a second network and mobile devices) - both are exercised manually against the running
app plus unit tests around logic that doesn't need real hardware/infrastructure. This document is
both the checklist and a record of what has actually been verified; unchecked items are open, not
assumed working.

## Automated

`desktop/tests/DekhBhai.Core.Tests` covers logic that's safe to unit test without a GPU/audio
device: session state transitions, the frame-content-hint sampling heuristic, and the
`SignalingMessage` wire format. Run with:
```
cd desktop
dotnet test
```

`signaling/test` covers the Phase 2 session/signaling layer with Node's built-in test runner:
session id/host-token generation and uniqueness, the session state machine's allowed/rejected
transitions, duration-to-expiry computation, viewer join/capacity/authorization, cleanup-tick
expiry and host-heartbeat-timeout detection, TURN credential minting (null-secret fallback,
coturn-shaped username, HMAC determinism), and host/viewer message validation (well-formed and
malformed cases). Run with:
```
cd signaling
npm test
```
28/28 passing as of the last run in this repo.

`desktop/tools/CaptureSmokeTest` is a standalone console app that starts `WindowsGraphicsScreenCapture`
alone (no encoder/WebRTC/UI) for 5 seconds and reports frame count/size/content hint - the
fastest way to confirm the capture engine itself works on a given machine before debugging
anything further up the pipeline.

## Manual checklist

### Core pipeline

- [x] Signaling server starts and responds to `GET /healthz`.
- [x] Host app connects to signaling on **START TEST STREAM**, receives a session id, and shows a
      share link.
- [x] Viewer page loads the share link and negotiates WebRTC (offer/answer/ICE) through the
      signaling relay.
- [x] Viewer reaches `connectionState: connected` and renders live video.
- [x] Verified via `RTCPeerConnection.getStats()` on a real run: `frameWidth`/`frameHeight`
      matched the display's native pixel resolution, `framesDecoded` growing with `framesDropped:
      0`, and `keyFramesDecoded > 0` - i.e. real, continuously decoding video, not a stalled or
      single-frame stream.
- [x] System audio: WASAPI loopback capture starts, Opus-encodes, and reaches the audio pipeline
      (`audio: capturing system audio` status). Full host-speaker-to-viewer-ear audio should be
      confirmed by ear on each test machine, since that step can't be verified by a script.

### Minimize / window lifecycle

- [x] After **START TEST STREAM**, the host window minimizes itself and calls
      `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.
- [x] Confirmed the window is not visible on the physical desktop after minimizing (screenshot
      comparison of the desktop before/after).
- [x] Restore the window from the taskbar while live and confirm the capture/stream keep running -
      verified on the installed MSIX build: restored the window mid-session, `viewers: 1` and
      `capture: capturing` both stayed correct, then minimized and stopped normally afterward.
- [x] **STOP TEST STREAM** runs the full teardown (capture → audio → encoders → WebRTC →
      signaling → resource release → confirmation) before the post-session screen appears; verified
      the signaling server logs `host disconnected` / viewer `left` in order, and the post-session
      screen ("Sharing Stopped" / "Designed by Aniket" / "START AGAIN") only rendered after that.
- [x] **START AGAIN** from the post-session screen returns to a fresh Live session with a new
      session id.

### Fullscreen / DRM / black-frame tolerance

The brief requires the pipeline to *not* treat a black or protected-content frame as failure, and
to keep working through normal fullscreen apps. Verify capture keeps running (viewer stays
connected, frame counter keeps advancing) with each of:

- [ ] Browser in fullscreen (F11)
- [ ] A video player in fullscreen
- [ ] PowerPoint in presentation mode
- [ ] VS Code maximized
- [ ] Normal windowed desktop use while switching apps

Dekh Bhai does not attempt to detect or bypass DRM/protected-content black frames - it just keeps
transmitting whatever WGC delivers (see `FrameState`/`FrameContentHint` in
`DekhBhai.Core.Capture`), so there is nothing app-specific to break here; this checklist exists to
catch a capture-engine regression, not DRM behavior itself.

**Black-frame pass-through/recovery** was verified directly rather than just asserted: a real
fullscreen black window was shown for 2 seconds while capturing and streaming. The trace log
showed a clean `Normal -> LikelyBlack -> Normal` transition with normal (tens-of-ms) frame
timing throughout, and the viewer's `getStats()` showed `framesDropped: 0` across the whole
window - i.e. genuinely no stall, crash, or stuck state going into or out of black content. See
`docs/architecture/phase-1-technology-decision.md` for the full investigation, including
reproduction attempts against the specific "black screen after a Windows screenshot action"
report (Win+Shift+S, Snipping Tool, GDI `CopyFromScreen`) - none reproduced a black frame on this
machine, but the pass-through/recovery behavior that scenario requires is confirmed working
regardless of the specific trigger.

### Host UI must not be captured

- [x] Verified the capture item is created for the **monitor**, not the Dekh Bhai window, and
      that the window is minimized + display-affinity-excluded on start, so it cannot appear in
      captured frames by construction.
- [x] Confirmed via `CaptureSmokeTest` output that a raw captured frame matches real desktop
      content pixel-for-pixel (visually verified once, then deleted - it necessarily contains
      whatever was on screen at the time).

### OS capture-indicator border

Investigated directly (see `docs/architecture/phase-1-technology-decision.md` for the full
write-up):

- [x] Confirmed a yellow line does appear briefly on the physical screen when capture starts -
      this is Windows' own Graphics Capture indicator (DWM-drawn), not Dekh Bhai.
- [x] Confirmed by simultaneous comparison (physical-screen screenshot vs. a pixel scan of the
      frame actually received by the viewer at the same moment) that the border is **not** present
      in the transmitted video - it's a local-only OS overlay.
- [x] Confirmed Dekh Bhai's own code never draws anything into the frame buffer (it only copies
      GPU texture bytes), so there's no code path that could add the border, a watermark, or any
      other overlay to captured content.
- [x] Implemented the supported suppression mechanism (MSIX package identity +
      `graphicsCaptureWithoutBorder` restricted capability + `GraphicsCaptureAccess.RequestAccessAsync`
      + `IsBorderRequired = false`) - see `docs/architecture/phase-1-technology-decision.md` and
      `docs/development/packaging.md`.
- [ ] The OS consent prompt for borderless capture was not interactively approved during
      (scripted) install testing, so whether the border actually disappears end-to-end on a real
      first run has not been visually reconfirmed since implementing this - do that with a human
      at the keyboard on the target machine.

### Installable build (MSIX)

Full checklist from `docs/development/packaging.md`, run against the actual installed package
(`Aniket.DekhBhai`, launched via its Start Menu shortcut / AUMID - not `dotnet run`, not the dev
build):

- [x] `dist/DekhBhai.msix` builds via `scripts/build-msix.ps1` and installs via
      `Add-AppxPackage` after trusting the signing cert.
- [x] Start Menu entry present (`Get-StartApps` lists "Dekh Bhai").
- [x] Installed to `C:\Program Files\WindowsApps\Aniket.DekhBhai_...\` - confirmed **not** the
      source/development directory.
- [x] Launched via `shell:AppsFolder\<AUMID>` (the same path a user double-clicking the Start
      Menu tile takes) - confirmed the running process's `Path` is under `WindowsApps`, not the
      repo.
- [x] START TEST STREAM → signaling connects, capture starts, system audio starts.
- [x] Viewer connects to the installed app's session and receives both video (`framesDecoded`
      growing) and audio (`bytesReceived` growing) via `getStats()`.
- [x] Minimize → viewer stays connected (`viewers: 1` unchanged) → restore → still live.
- [x] STOP TEST STREAM → signaling log shows clean `host disconnected` / viewer `left` teardown.
- [x] Post-session screen shows **"Sharing Stopped"** and **"Designed by Aniket"**.
- [x] START AGAIN → fresh Live session, capture and audio both restart successfully.
- [x] `Remove-AppxPackage` uninstalls cleanly: package gone from `Get-AppxPackage`, Start Menu
      entry gone, `C:\Program Files\WindowsApps\Aniket.DekhBhai_...` directory deleted.
- [ ] Not tested on a second, separate physical Windows machine (only this dev machine, via a
      real install/uninstall/reinstall cycle) - the whole point of the self-contained MSIX build
      is that it *should* need nothing else, but a true "friend's laptop" test hasn't happened.

### Cross-browser viewer

- [x] Chrome (primary target, used for all testing above).
- [ ] Edge, Firefox - not yet tested; the viewer uses only standard `RTCPeerConnection`/media
      element APIs, so it should work, but per the brief this should not be *claimed* without
      testing.

## Phase 2 checklist

Code-level items (session model, protocol, security validation) are covered by the automated
signaling/desktop tests above. This section is the manual/infrastructure-dependent checklist -
see `docs/architecture/phase-2-technology-decision.md` for what each item's implementation looks
like and `docs/deployment/phase-2.md` for the deployment procedure these tests depend on.

### Session lifecycle and duration

- [x] `create-session` -> `STARTING` -> `host-live` -> `LIVE` -> `stop-session` -> `STOPPING` ->
      `STOPPED` transition sequence enforced and unit-tested (`sessionStateMachine.test.js`
      equivalents inside `sessionStore.test.js`).
- [x] Invalid transitions throw rather than silently changing state (unit test).
- [x] Fixed-duration sessions compute a real `expiresAt`; `untilStopped` never does (unit test).
- [x] Viewer capacity (`MAX_VIEWERS_PER_SESSION`) enforced (unit test).
- [ ] **Real-time auto-expiration observed end-to-end** with a live host + viewer (using
      `TEST_DURATION_OVERRIDE_MS` to avoid waiting out a real 15-minute/1-hour/5-hour session):
      viewer receives `session-expired`, host stops capture/audio/WebRTC, post-session screen
      appears. Logic is unit-tested in isolation; a live run through the actual desktop app +
      browser has not been performed in this session.
- [ ] Expired session's link rejects a new viewer with `SESSION_EXPIRED` state, observed live
      (not just via the `addViewer`/`isJoinable` unit test).

### Production signaling and security

- [x] Malformed/unknown host and viewer messages rejected without a crash (unit tests in
      `validate.test.js`).
- [x] A wrong or missing host token is rejected (`authorizeHost` unit test) - one host cannot
      control another session.
- [x] Per-connection rate limiting implemented (`rateLimiter.js`); not yet exercised against a
      real flood from a live client.
- [ ] Production WSS/HTTPS deployment reachable from the public Internet - **not deployed**; no
      domain/TLS/hosting has been provisioned in this environment (see
      `docs/deployment/phase-2.md`).
- [ ] `/health`, `/ready` respond correctly behind a real reverse proxy/load balancer in that
      deployment.

### QR code and link

- [x] QR code and "Copy Link" both render the exact same `{ViewerBaseUrl}/v/{sessionId}` URL
      (`MainWindow.xaml.cs: OnShareUrlReady` sets both from the same `url` value - verified by
      code inspection, not yet re-confirmed with a physical phone-camera scan in this session).
- [ ] Physical QR scan -> phone browser opens -> correct session -> live video, confirmed on a
      real device.

### TURN / cross-network connectivity

- [ ] **Critical, not yet done.** A coturn instance has not been deployed (see
      `docs/deployment/phase-2.md` section 3). TURN credential minting is implemented and
      unit-tested in isolation (`turnCredentials.test.js`), but no real WebRTC connection has
      been forced through a live TURN relay and confirmed via `getStats()` candidate-pair type
      (`relay` vs `srflx`/`host`).
- [ ] Host and viewer confirmed connecting successfully from two different Internet connections
      (not just different devices on the same LAN).

### Viewer states and reconnection

- [x] All six viewer states implemented (`CONNECTING`, `LIVE`, `RECONNECTING`, `SESSION_ENDED`,
      `SESSION_EXPIRED`, `SESSION_UNAVAILABLE` - `viewer.js: VIEWER_STATE`), with backoff-based
      reconnect on unexpected WebSocket close (`RECONNECT_DELAYS_MS`).
- [ ] Reconnect-after-viewer-refresh, and recovery from a real temporary network interruption
      (e.g. toggling Wi-Fi off/on mid-session), observed live rather than just by code inspection.
- [x] A momentary `disconnected` peer-connection state is tolerated with a grace period before
      being treated as gone, both host-side (`WebRtcHost.DisconnectedGracePeriod`, 12s) and
      viewer-side (only `failed`/`closed` trigger a reconnect, not `disconnected`) - implemented,
      not yet exercised against a real flaky network.

### Multiple viewers

- [x] Signaling layer enforces and reports viewer count/capacity correctly (unit tests).
- [x] One capture/encode pipeline fans out to multiple `RTCPeerConnection`s
      (`WebRtcHost.SendVideo`/`SendAudio` iterate all connected viewers) - by construction, not
      per-viewer capture.
- [ ] 3+ concurrent real viewers, confirmed all receiving live video/audio simultaneously. Not
      load-tested.

### Installable build (MSIX) - production configuration

- [x] Phase 1 MSIX install/launch/uninstall lifecycle (see below, carried over).
- [ ] Installed MSIX pointed at a real production `DEKHBHAI_SIGNALING_WS_URL`/
      `DEKHBHAI_VIEWER_BASE_URL` (machine environment variables, post sign-out/in) and confirmed
      connecting to a live public deployment - blocked on the deployment itself not existing yet.
      See `docs/deployment/phase-2.md` section 5 ("Current known MSIX issue") for the root-cause
      analysis of the previously reported "failed to start: Unable to connect to the remote
      server" failure.

### End-to-end test matrix (from the Phase 2 brief)

| # | Test | Status |
|---|---|---|
| 1 | Local: host + viewer, same machine/network | Not re-run against the current `/v/<id>` URL shape in this session - was verified pre-Phase-2 against the query-string shape (see the Phase 1 section above); protocol changes since then are covered by unit tests but not a fresh manual pass. |
| 2 | Host laptop + second physical laptop | Not performed - needs a second machine. |
| 3 | Host and viewer on different Internet connections | **Not performed - blocked on TURN deployment (critical, see above).** |
| 4 | Android browser viewer | Not performed - needs a physical Android device. |
| 5 | iOS browser viewer | Not performed - needs a physical iOS device. |
| 6 | Minimize host, continue using laptop | Verified in Phase 1 against the old UI; not re-verified against the redesigned Phase 2 host UI. |
| 7 | Viewer fullscreen, Esc does not end session | Implemented (`fullscreenBtn` only calls `requestFullscreen`, never touches `ws`/`pc`) - not manually re-verified this session. |
| 8 | Stop Sharing full teardown sequence | Verified in Phase 1 against the old UI; not re-verified against the redesigned Phase 2 host UI/protocol. |
| 9 | Auto-expiration (shortened test duration) | Not performed - see "Session lifecycle" above. |
| 10 | Start -> Stop -> Start (second session works) | Verified in Phase 1; not re-verified since. |
| 11 | Viewer refresh/rejoin mid-session | Not performed. |
| 12 | Network interruption / reconnect | Not performed. |
| 13 | Black frame pass-through | Verified in Phase 1 (see above) - viewer-side "don't error on disconnected" behavior added since has not been re-tested against a real black-frame window. |
| 14 | Screenshot-action false-positive check | Verified in Phase 1 (see above). |

**Do not treat any unchecked item above as working** - the code implementing it exists and, where
noted, is unit-tested in isolation, but the brief is explicit that untested claims (TURN, mobile,
cross-network) must not be made. These are the concrete next steps once real deployment
infrastructure (domain, TLS, coturn, a second network, and mobile devices) is available.
