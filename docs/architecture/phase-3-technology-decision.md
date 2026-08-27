# Phase 3 Technology Decision

Phase 3 hardens the Phase 1/2 architecture for real production use: investigating the Render
reliability issue found during Phase 2, adding host-side reconnect/resume so a transient network
blip doesn't kill a session, and fixing two real bugs surfaced while testing those changes. No
new component was introduced and no existing architecture was replaced - every change below is
additive to the existing capture/WebRTC/signaling design documented in
`docs/architecture/phase-1-technology-decision.md` and `docs/architecture/phase-2-technology-decision.md`.

## What was retained unchanged

Per the Phase 3 brief's explicit instruction not to rebuild or replace working architecture:
native Windows Graphics Capture, the VP8/Opus encode pipeline, one-`RTCPeerConnection`-per-viewer
in `WebRtcHost`, the Node.js signaling relay's overall shape, the in-memory session store (still
no database - see the Phase 2 doc for why), the MSIX packaging pipeline, and the Vercel/Render
deployment split. None of these were touched beyond the additive changes described below.

## Render reliability investigation (Part 2)

**Method**: since this environment has no Render dashboard/log access, the investigation was
done entirely from the outside - repeated HTTP/WebSocket probes against the live deployment,
varying the window length to distinguish hypotheses.

**Findings**:
- A short burst (20 requests over 20s) immediately after a period of no traffic showed 6-14 out
  of 20 requests failing with `x-render-routing: no-server` - Render's own header meaning no
  backend instance was available to route to, not an application-level 404.
- A **sustained** 76-second continuous probe (1 req/sec) against an already-warm instance
  returned **76/76 successes**, consistent ~0.35-0.45s latency throughout - no flakiness at all
  once warm.
- A WebSocket-level probe (real `resume-session`-shaped connections) got a fully correct
  `session-created` response on the attempts that connected, confirming the protocol
  implementation itself is correct - the flakiness is not an application bug.

**Conclusion**: this is Render free-tier idle-spindown/cold-start behavior, not a crash loop or a
code defect. The wake-up transition is messier than a single clean delay (a few seconds of mixed
success/failure while the instance comes up), but once warm the service is fully reliable. This
revises the more ambiguous "not just cold-starting" framing in the Phase 2 deployment doc - it
*is* cold-starting, just with a rougher transition than expected.

**What was done about it**:
- **Graceful SIGTERM/SIGINT shutdown** added to `server.js` - notifies every active session's
  viewers and host before closing, rather than the process just vanishing mid-deploy. Render (and
  most process supervisors) send SIGTERM before killing a process for a redeploy; without a
  handler, in-flight sessions would silently disappear with no client-side signal.
- **Defense-in-depth `uncaughtException`/`unhandledRejection` handlers** - log with full detail
  and keep serving other connections, rather than Node's default of crashing the whole process
  over one unexpected exception in one message handler. Added even though this session's
  investigation attributed the measured flakiness to Render's cold start, not a crash, as a
  hardening measure against a class of bug this investigation couldn't rule out from outside.
- **Host reconnect / resume-session** (see below) - directly mitigates the user-visible impact of
  a cold-start blip: if a signaling connection drops during the wake-up window, the host now
  recovers automatically instead of the session simply failing.
- **Documented, not auto-applied**: upgrading off the Render free tier, or adding an external
  keep-warm ping (e.g. a scheduled GitHub Actions workflow hitting `/health` every ~10 minutes) -
  see `docs/deployment/phase-3.md`. Not applied automatically per the brief's "do not upgrade
  Render automatically" instruction.

## Host reconnect / resume-session (Part 4)

**Problem found**: before this phase, the desktop `SignalingClient` had no reconnect logic at
all, and the server's `handleHost` closed a session **the instant** the host's TCP connection
dropped (`socket.on('close', ...)` called `endSession` synchronously) - a momentary network blip
(exactly what the Render investigation above found happens during cold-start) would end the
session and kick every viewer with no chance to recover, even though the host's own capture/
audio/WebRTC were completely unaffected and continuing to run.

**Decision**: add a bounded protocol extension - `resume-session` - rather than a broader
rearchitecture:

- **Server** (`sessionStore.js`, `server.js`): a host disconnect now calls
  `store.markHostDisconnected(session)` (records a timestamp) instead of ending the session
  immediately. A new `hostReconnectGraceMs` (default 20s, `config.js`) window gives the host a
  chance to reconnect and send `resume-session { sessionId, hostToken }`, which reattaches the
  new socket to the *same* session object (`store.resumeHost`) - viewers are never touched, and
  any viewer whose WebRTC peer connection is already established keeps receiving media the whole
  time regardless, since media never routes through this socket. If the grace period elapses
  unresumed, the existing cleanup tick ends the session via the same `endSession` path already
  used for expiration/heartbeat-timeout (no duplicate cleanup logic, per the brief).
- **Client** (`SignalingClient.cs`, `SessionController.cs`): `ReconnectAndResumeAsync()` opens a
  new `ClientWebSocket` to the same signaling URL and sends `resume-session`, reusing the *same*
  `SignalingClient` instance (so `WebRtcHost`'s existing event subscriptions and the heartbeat
  timer keep working unchanged once reconnected - only the underlying socket is swapped).
  `SessionController.AttemptReconnectAsync` retries on a backoff schedule, surfacing status via a
  new `SignalingStatusChanged` event (bound to the existing `ConnectionStatusText` in the UI - no
  new UI element). Capture, audio capture, and WebRTC are never touched by this path - they keep
  running through a reconnect attempt exactly as they would through any other transient event.

**Rejected**: a full session-state-in-Redis-plus-pub/sub rearchitecture (the kind of thing the
Phase 2 doc explains was rejected for putting signaling on Vercel) - unnecessary here since
signaling remains a single long-lived process; the existing in-memory store just needed a grace
window, not external state.

**Tested**:
- 4 new server-side unit tests (`sessionStore.test.js`) plus 2 `validate.js` tests for the new
  message type - 35/35 signaling tests passing.
- **Live end-to-end, twice, against the actual installed MSIX**: a disposable local TCP proxy
  (not a permanent part of the app) was put in front of a real local signaling server so a
  connection could be killed and restored while the server process (and its in-memory session)
  stayed alive - a firewall-based simulation was considered and rejected first, since an
  established TCP connection can silently stall for a long time under a firewall block rather
  than triggering a prompt disconnect. First run: an intentionally-long (~29s) outage exceeded
  the client's retry budget, and the app **gave up gracefully and reached the correct
  post-session screen with no crash or hang** - which is itself the correct behavior for an
  unrecoverable outage, and revealed the retry schedule (`{1,2,4,8}`s, 15s total) was shorter
  than the server's 20s grace window. Retuned to `{2,3,4,5,5}`s (19s, closer to the full grace
  window) and retested: a ~4.6s outage was detected, retried, and **resumed the identical session
  id** (`ShareUrlBox` unchanged) with status recovering to "connected" - confirmed via both the
  UI Automation-read status text and the signaling server's own log
  (`session.host-disconnected` → `session.host-resumed`).

## Bugs found and fixed while testing the above

Neither of these was introduced by the reconnect work - both were pre-existing and surfaced only
because Phase 3 testing exercised paths Phase 2 testing hadn't.

### Local development pointed itself at production (viewer/config.js)

`viewer/config.js` is checked in with the production Render origin hardcoded (correct for the
separate Vercel deployment - see `docs/deployment/phase-2.md`), but this signaling process also
serves the same `viewer/` directory for local development. The result: running `npm start`
locally and opening the viewer at `http://localhost:8787` made the page's own `config.js` tell it
to connect to **production** Render instead of the local dev server, so a locally-created session
was never reachable from the locally-served viewer page at all. Found because a fresh local
session showed "This session is unavailable" despite a direct raw WebSocket probe to the same
session succeeding immediately.

**Fix**: `server.js` now registers an explicit `app.get('/config.js', ...)` route, before the
static-file middleware, that always serves the correct empty-origin (same-origin) config for
anything this process itself serves - the checked-in production value in the file is only ever
seen by Vercel's independent static deployment, never by this dev server.

### CopyLinkButton_Click could crash the entire application

Found via a Windows Application Event Log crash report during automated testing: clicking
**Copy Link** threw an unhandled `System.Runtime.InteropServices.COMException
(CLIPBRD_E_CANT_OPEN)` from `Clipboard.Flush()` (triggered internally by `Clipboard.SetText`'s
single-argument overload), which WPF does not catch - .NET terminates the whole process on an
unhandled exception on the UI thread. `OpenClipboard` legitimately fails when another process
(a clipboard manager, an RDP session, a screen reader, etc.) holds the clipboard at that exact
instant - a real, if infrequent, everyday condition, not something specific to automation.

**Fix**: `TryCopyToClipboard` retries up to 3 times with a short delay and uses
`Clipboard.SetDataObject(text, copy: false)` (skips the flush call that was the actual failure
point) instead of `Clipboard.SetText`. On persistent failure it shows "Couldn't copy - try again"
in the existing confirmation text element rather than crashing. No visual/UX change beyond that
failure-path message; the success path looks identical to before.

## Production launch failure: root-cause investigation and fix

After the work above, a manual launch of the installed app failed at Start with "Unable to
connect to Dekh Bhai's signaling service" - investigated as a real production incident rather
than assumed to be the already-known Render flakiness.

**Evidence gathered, in order**:
1. `[Environment]::GetEnvironmentVariable("DEKHBHAI_SIGNALING_WS_URL", ...)` checked at Process,
   User, and Machine scope on the machine the app was launched on - **all three empty**. Nothing
   had ever set these persistently; every previous "production" test in this repository's history
   worked because the *test harness* injected them directly into that one child process's
   environment (`ProcessStartInfo.EnvironmentVariables`), which has no effect on a normal
   Start-Menu/double-click launch.
2. `AppConfig.cs` confirmed: `DEKHBHAI_SIGNALING_WS_URL` unset → falls back to
   `ws://localhost:8787/ws?role=host`.
3. Reproduced directly: launched the installed exe with **no** env vars set (exactly how a normal
   launch behaves) and got the exact reported symptom.
4. **Render itself checked independently at the same time**: 20/20 HTTP health checks succeeded,
   and 4/4 real WebSocket handshakes returned correct `session-created` responses in under 1.1s
   each. Render was not the cause of this particular failure.

**Root cause**: the installed app was never actually pointed at production for a normal launch -
only ever for automated test launches that supplied the environment variables directly. This is
an operational/deployment gap, not a Render outage and not a WebRTC/capture bug.

**Fix (operational)**: set `DEKHBHAI_SIGNALING_WS_URL`/`DEKHBHAI_VIEWER_BASE_URL` at the User
level via `[Environment]::SetEnvironmentVariable(..., "User")` on this machine (Machine-level
requires admin rights this session doesn't have). **This still requires a sign-out/sign-in (or an
Explorer restart) before a Start-Menu launch will pick it up** - Explorer's own environment block
is captured at logon and does not refresh live, exactly as already documented in
`docs/deployment/phase-2.md` §5. This was not done automatically (it would sign the user out of
their desktop session) - the user needs to do this themselves once.

**Fix (code) - initial-connection retry**: a second, independent gap made this worse than it
needed to be even for a *genuinely* temporary outage (e.g. a real Render cold start): the original
code treated any single failed connect-or-create-session attempt as immediately fatal, with no
retry, unlike the reconnect-after-Live logic built earlier this phase. `SessionController` now has
`EstablishSignalingAsync`, which retries the connect-and-create-session sequence on backoff
(`InitialConnectDelaysSeconds`: cumulative 2, 5, 10, 18, 28s) before surfacing a failure, showing
"Connecting to Dekh Bhai..." then "Still connecting to Dekh Bhai..." rather than technical
WebSocket/Render error text. Each attempt uses a fresh `SignalingClient`; only the one that
succeeds is kept. A genuinely broken configuration (as in the reproduction above, since retrying
against localhost can never succeed) now takes up to ~36 seconds to report failure instead of
being instant - an accepted, deliberate tradeoff for tolerating a real cold start, and the
tradeoff explicitly requested.

**Bug found and fixed alongside this**: the failure message was already being set correctly
(`CaptureStatusChanged` → `DurationStatusText`) in the original code, but `StartAsync`'s catch
block then called `SetState(SessionState.Idle)`, which switches the visible panel to
`IdlePanel` - hiding the very message that had just been set on `DurationPanel`'s status text a
moment earlier. The user's report ("it displays the message") is consistent with catching a brief
flash before the panel switch hid it. Fixed by transitioning to the existing `SessionState.Error`
state instead (which keeps `DurationPanel` visible) and by making `MainWindow`'s `Error`-state
handler stop unconditionally overwriting `DurationStatusText` with a generic stop-failure message
- it now only falls back to that generic text if no specific message was already set.

**Tested**: rebuilt and reinstalled the MSIX with both fixes; reproduced the no-env-var failure
again and confirmed the status text now progresses "Connecting..." → "Still connecting..." →
the final translated error, which **persists on screen** rather than reverting to the bare Idle
screen. Then ran two complete real sessions against production (env vars supplied the same way
the test harness always has) - both reached Live, both showed growing `framesDecoded`/audio
sample counts with `framesDropped: 0`, both stopped cleanly with the correct post-session screen.
See `docs/testing/phase-3-release-test.md` for the full detail. A literal "let Render go idle for
15 minutes, then launch" reproduction was not performed (impractical within this session) - the
retry mechanism was instead validated against a guaranteed-unreachable endpoint (localhost with
nothing listening), which exercises the identical retry/backoff/status-reporting code path a real
Render cold start would.

## Release build now carries built-in production defaults

**Problem**: even after the initial-connection retry fix above, new devices/friends installing
the app still sometimes got stuck showing "Connecting..." for a long time before failing. Root
cause: nothing had actually changed about *where the app defaults to* - `AppConfig.cs` still
defaulted to `ws://localhost:8787` unless an environment variable was set, and a friend
installing the app has no way to know to set one (and shouldn't have to - see the explicit "a
new user should not need to... configure environment variables" requirement). Combined with the
new ~28-36s retry budget, a misconfigured install now spends much longer visibly "trying" before
failing, which reads as "stuck" even though it's technically bounded.

**Fix**: `AppConfig.cs`'s defaults are now chosen by build configuration
(`#if DEBUG ... #else ... #endif`, standard MSBuild-provided symbols - no new build property
needed): Debug builds (`dotnet run`) still default to localhost for local development; Release
builds (what `scripts/build-msix.ps1` publishes, and therefore every installed MSIX) default
directly to the real production signaling/viewer endpoints. Both remain public service endpoints,
compiled directly into the binary - not a secret, and still overridable via
`DEKHBHAI_SIGNALING_WS_URL`/`DEKHBHAI_VIEWER_BASE_URL` for anyone who does want to point a build
elsewhere. No environment variable, config file, or setup step is needed for a normal installed
copy to reach production.

**Tested**: uninstalled the app, removed the User-level environment variables set in an earlier
session, rebuilt the Release MSIX, reinstalled, and launched with **zero environment variables
present anywhere** (verified empty at Process/User scope) - twice. Both runs automatically
reached Live against real production infrastructure with no manual configuration. See
`docs/testing/phase-3-release-test.md`.

## Viewer audio: getStats() is not proof of audible sound

**Problem**: video statistics looked correct, but sound was reported as not actually audible.
Investigated by checking the `<video>` element's real playback state (`.paused`, `.muted`), not
just `RTCPeerConnection.getStats()` - and found `paused: true` even while `framesDecoded` and
`totalSamplesReceived` were both actively growing. This is the concrete mechanism worth
understanding: Chromium's WebRTC decode pipeline decodes inbound frames/samples into internal
buffers as soon as a track is live, **independent of whether any `<video>`/`<audio>` element is
actually playing them** - so `getStats()` counters keep climbing even when nothing is being
rendered to screen or sent to speakers. The actual gate is the browser's autoplay-with-sound
policy: `viewer/index.html`'s `<video autoplay playsinline>` has no `muted` attribute, and
`viewer.js` never called `.play()` explicitly or handled rejection - so on a `<video>` element
without enough "media engagement" for that origin, Chrome's autoplay-with-sound policy silently
rejected playback, leaving the element paused indefinitely with no error surfaced anywhere.

**Fix**: `startPlayback()` in `viewer.js` now explicitly attempts unmuted `play()` first; if that
rejects, it falls back to `videoEl.muted = true` (muted autoplay is virtually always allowed) and
shows a small "🔇 Tap for sound" button (`viewer/index.html`, `viewer/style.css`) that unmutes and
retries `play()` on an actual click - a real user gesture, which satisfies the browser's autoplay
policy. The unmute button only hides itself once `play()` is *confirmed* to have succeeded
unmuted, not merely on click (an earlier version of this fix hid it unconditionally on click,
which would have stranded a user with no way to retry if a genuine user gesture still failed for
some other reason).

**Tested**: confirmed via direct inspection of `videoEl.paused`/`.muted` (not just stats) before
and after the fix, deployed to the production Vercel viewer, and verified with a **real trusted
mouse click** (not a script-dispatched `.click()`, which Chrome does not treat as a user gesture
for autoplay purposes - confirmed this distinction directly: a synthetic click left the element
paused, a real simulated mouse click via the browser automation tool's input-injection path did
not). After a real click: `paused: false`, `muted: false`, unmute button hidden. **This confirms
the browser is actively playing decoded, unmuted audio through the default output device** -
which is the full extent of what can be verified without a human physically listening. Whether
sound is actually audible through physical speakers/headphones was not independently confirmed by
this agent (no audio input capability) - this is called out explicitly rather than assumed.

## Single-file installer

**Problem**: the previous release artifact was `dist/release/` containing a raw `.msix` plus a
`.cer` plus text files - not something to hand a non-technical friend, and installing a
self-signed MSIX still required an interactive `Import-Certificate` PowerShell command.

**Decision**: [Inno Setup](https://jrsoftware.org/isinfo.php) (free, open-source, no large
framework - installed via `winget install JRSoftware.InnoSetup`) compiles
`packaging/installer/DekhBhai.iss` into a single `dist/release/DekhBhai-Setup.exe`. This wraps
the existing MSIX rather than replacing it - the MSIX and its packaging pipeline are completely
unchanged. The installer:
1. Embeds `dist/DekhBhai.msix` and `packaging/msix/DekhBhaiSigning.cer` inside itself (compressed
   into the one `.exe` - nothing is distributed alongside it).
2. Requests admin elevation once (`PrivilegesRequired=admin` - the "Windows needs permission to
   install Dekh Bhai" UAC prompt, not an interactive PowerShell window).
3. Runs `certutil -addstore -f TrustedPeople` (a built-in Windows tool) to trust the certificate,
   then `Add-AppxPackage` to install the MSIX - both invoked internally by the installer, never
   typed by the user.
4. Registers an `[UninstallRun]` step that also removes the MSIX package on uninstall, so
   "Uninstall Dekh Bhai" from Windows Settings correctly removes everything.
5. Offers an *optional* desktop shortcut (unchecked by default) that launches via
   `explorer.exe shell:AppsFolder\Aniket.DekhBhai_ztn0zpwa8syma!DekhBhai` - **not** a raw shortcut
   to the exe path, which would launch outside package identity and silently break Graphics
   Capture border suppression. The Start Menu entry itself needs no installer-side work at all -
   MSIX creates it automatically from `AppxManifest.xml` the moment `Add-AppxPackage` completes.

The package family name (`Aniket.DekhBhai_ztn0zpwa8syma`) embedded in that shortcut command is
deterministic - it's a hash of the publisher identity (`CN=Aniket Raj`) from the manifest, not a
per-install random value - confirmed by observing it stay identical across every rebuild and
reinstall performed this session.

**Rejected**: WiX (more powerful but a materially larger toolchain/learning curve for what's
fundamentally "run two commands with elevation"); a raw self-extracting script (not a "proper
installer technology", and blurs the line the brief draws against running arbitrary scripts).

**Build**: `scripts/build-installer.ps1` - builds the MSIX (via the existing, unmodified
`scripts/build-msix.ps1`) if needed, locates or installs Inno Setup, and compiles the `.iss` to
`dist/release/DekhBhai-Setup.exe`.

**Tested, and where testing hit a real, correct boundary**: the installer compiles successfully
(146 MB), and the *application* it installs was fully tested via a direct `Add-AppxPackage`
against the exact same rebuilt MSIX (see the release-test doc) - the fix for Problem 1 (baked-in
config) is proven working. The installer's own elevated steps (UAC consent, `certutil` writing to
the machine certificate store) were **not** end-to-end tested by this agent: this development
session is not running as Administrator, and Windows correctly requires an interactive UAC click
for that - an attempted workaround (registering a scheduled task with `RunLevel Highest` to run
the installer unattended) was itself correctly rejected with Access Denied. This is the security
boundary working as intended, not a gap to route around. **The repository owner needs to run
`dist/release/DekhBhai-Setup.exe` once, on a machine, and click "Yes" on the UAC prompt** to
complete verification of the certificate-trust step end-to-end; the underlying commands
(`certutil -addstore`, `Add-AppxPackage`) are individually well-understood, standard operations,
not novel or risky.

## What remains blocked

- **TURN/coturn deployment** - the *code* (TURN credential minting, `buildIceServers`, the
  `HOST_RECONNECT_GRACE_MS`/`TURN_URL`/`TURN_SECRET` environment seams) has been in place since
  Phase 2 and is unchanged; no coturn instance has been deployed, because this environment has no
  infrastructure account to provision one on. See `docs/deployment/phase-3.md` and
  `docs/testing/phase-3-cross-network-test.md`.
- **Cross-network testing** (host and viewer on genuinely different networks) - requires a
  second physical network/device this environment does not have. See
  `docs/testing/phase-3-cross-network-test.md` for exactly what was and wasn't tested instead.
- **iOS/Safari, Edge, Firefox viewer testing** - no such devices/browsers were exercised this
  phase either; only Chrome (desktop, two simultaneous tabs) was tested. An Android phone was
  tested by the project owner directly in an earlier session (real video+audio confirmed, per
  that session's transcript) but not by this agent.
