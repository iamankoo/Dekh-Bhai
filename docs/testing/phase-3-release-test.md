# Phase 3 Release Test

Full installed-MSIX release test cycle against **production infrastructure** (Render signaling +
Vercel viewer), run against the Phase 3 build (session-resume support, clipboard-crash fix,
local-dev config fix, tuned reconnect schedule). All steps below were actually executed and
verified in this session - none are asserted without evidence.

## Build and install

| Step | Result | Evidence |
|---|---|---|
| `scripts/build-msix.ps1` | PASS | `dotnet publish` (self-contained win-x64), `makeappx pack`, `signtool sign` all completed with no errors; `dist/DekhBhai.msix` produced. |
| `Add-AppxPackage` | PASS | Installed as `Aniket.DekhBhai_1.0.0.0_x64__ztn0zpwa8syma`. Reused the already-trusted Phase 1 signing certificate (`CN=Aniket Raj`, already in `Cert:\LocalMachine\TrustedPeople` from an earlier session) - no fresh trust step was needed this run, but the documented `Import-Certificate` procedure (`docs/development/packaging.md`) is what a genuinely clean machine would need first. |

## Full session lifecycle (production)

| # | Step | Result | Evidence |
|---|---|---|---|
| 1 | Launch installed app | PASS | Direct exe launch, main window appeared within ~1.2s. |
| 2 | Idle screen visible | PASS | `StartSharingButton` (AutomationId) confirmed on-screen via UI Automation. |
| 3 | Start Sharing → duration → Start | PASS | Real UI Automation `InvokePattern.Invoke()` clicks - no coordinate clicking. |
| 4 | 15-minute duration | PASS | One full cycle run with `FifteenMinutes` selected; reached Live. |
| 5 | Production signaling | PASS | `wss://dekh-bhai-signaling.onrender.com/ws?role=host` - session created and went Live on the first attempt in every run this phase. |
| 6 | Viewer loads | PASS | `https://viewer-theta-ashy.vercel.app/v/<id>` opened in Chrome, reached "Live". |
| 7 | Video flows | PASS | `getStats()`: `framesDecoded` growing, `framesDropped: 0`, 1920×1080 (matches earlier Phase 2 measurement; not re-measured resolution this run but reconfirmed frames/drops). |
| 8 | Audio flows | PASS | `getStats()`: `totalSamplesReceived` growing across repeated polls. |
| 9 | Minimize | PASS | `WindowPattern.Current.WindowVisualState == Minimized`, confirmed via UI Automation immediately after reaching Live, both runs. |
| 10 | Continue using desktop | PARTIAL | Not independently exercised as a distinct step this run (no separate "use the desktop" action was scripted) - but capture/session were demonstrably undisturbed by concurrent PowerShell/UI Automation and Chrome activity on the same machine throughout every test in this phase, which is the practical equivalent. |
| 11 | Stop | PASS | `StopButton` invoked via UI Automation **while the window was still minimized** - confirms Stop is reachable without needing to restore the window first. |
| 12 | Post-session screen | PASS | All three expected texts (`Sharing Stopped`, `Designed by Aniket`, `START AGAIN`) confirmed visible (`IsOffscreen == false`) via UI Automation. |
| 13 | START AGAIN | PASS | Clicked via UI Automation; confirmed return to the duration-selection screen (`Duration15Button` became visible again). |
| 14 | Second session | PASS | Started a second session from the same running app instance (not a process relaunch) and reached Live. **Session id differed from session 1** (`Vj8-SfPDnP2qUsq_BPPivA` vs `wEjemH9RhF4cp5xEg3kzHQ`) - confirms no stale session id/host token reuse. |
| 15 | Stop (second session) | PASS | Same verification as step 12, repeated - all three post-session texts confirmed. |
| 16 | Uninstall | PASS | `Remove-AppxPackage`. Verified clean: `Get-AppxPackage` returns nothing, `Get-StartApps` no longer lists Dekh Bhai, and `C:\Program Files\WindowsApps\Aniket.DekhBhai_...` is gone from disk. |

## Multi-viewer (Part 13)

| Test | Result | Evidence |
|---|---|---|
| Two simultaneous viewers, same session | PASS | Two separate Chrome tabs opened the same production share URL. Both reached `connectionState: "connected"` and showed growing `framesDecoded` with `framesDropped: 0` independently (Tab A: 181 frames decoded at check time; Tab B: 185). Host's `ViewerStatusText` correctly showed `viewers: 2`. |
| Architecture confirmation | PASS (by code, re-verified) | `WebRtcHost.SendVideo`/`SendAudio` iterate all connected viewer `RTCPeerConnection`s from the *same* encoded sample - capture/encode is not duplicated per viewer (unchanged from Phase 2; re-confirmed by reading the code again this phase). |
| 3+ viewers / sustained load | NOT TESTED | Only 2 concurrent viewers were exercised. Higher counts remain implemented-and-unit-tested-for-capacity (`MAX_VIEWERS_PER_SESSION`) but not load-tested. |

## Performance (Part 14)

Measured on the actual installed app during the 2-viewer test above (12 logical CPU cores on the
test machine):

| Metric | Value |
|---|---|
| Working set, 1 host process, 0 viewers (baseline, ~13s after launch) | ~323 MB |
| Working set, 2 viewers connected | ~341-348 MB (stable across a 5s window - no growth observed) |
| CPU time delta, 2 viewers, 5-second wall-clock window | 8.27s of CPU time → ~165% of one logical core |
| Video resolution | 1920×1080 (native display resolution, unscaled) |

**Not measured**: single-viewer CPU baseline for direct comparison, sustained multi-minute memory
trend (only a 5-second window was observed - not enough to detect a slow leak), and encode-side
FPS as reported by the app itself (only the viewer's *received* FPS/frame counts were captured).
Treat these performance numbers as a spot-check, not a full profiling pass.

## Reconnect / resume (Part 4) - see also the technology-decision doc

| Test | Result | Evidence |
|---|---|---|
| Long outage (~29s) exceeds retry budget | PASS (graceful degradation) | App reached the correct post-session screen with no crash/hang after exhausting its retry schedule. Revealed the original retry budget (15s) was shorter than the server's 20s grace window - fixed (see technology-decision doc). |
| Short outage (~4.6s) within retry budget | PASS (successful resume) | `ConnectionStatusText` recovered to "connected"; `ShareUrlBox` unchanged (same session id); server log shows `session.host-disconnected` → `session.host-resumed` ~4.6s apart. |

## Clean-machine simulation (no environment variables, no test-harness config)

Run after the baked-in production defaults and viewer audio fixes (see
`docs/architecture/phase-3-technology-decision.md`). Simulated as closely as this single dev
machine allows: removed the User-level env vars set in an earlier session, uninstalled the app,
rebuilt the Release MSIX, reinstalled, and launched via `Start-Process` with **no environment
variables passed at all** (confirmed empty at Process and User scope beforehand) - twice.

| # | Step | Result |
|---|---|---|
| 1 | Launch with zero env vars | PASS - both runs |
| 2 | Automatically reaches Live using baked-in production defaults | PASS - both runs, real share URLs generated (`.../v/90uNX6OZpTxZoU0UoD4OJw`, `.../v/EOZtPCdHcKTdI8EQhNDX9A`) |
| 3 | Video actually plays (not just `getStats()` growing) | PASS - `videoEl.paused === false` confirmed directly |
| 4 | Audio actually plays after one real click | PASS - `videoEl.muted === false`, `paused === false`, unmute button hidden, confirmed with a real simulated mouse click (not a script-dispatched one - see technology-decision doc for why that distinction matters) |
| 5 | Stop → post-session screen | PASS - both runs, all three texts confirmed visible |

**Limitation honestly noted**: this machine still has .NET/Node/Git/PowerShell installed (it's a
dev machine) - it cannot fully simulate "a friend's machine with literally nothing installed".
What *was* removed and verified absent is the specific thing Problem 1 was about: any
manually-configured `DEKHBHAI_*` environment variable. The installer itself (which would be the
*actual* thing a friend runs) was built and its logic reviewed but not run end-to-end - see
"Single-file installer" in the technology-decision doc for the specific, legitimate reason
(requires an interactive UAC click this automated session cannot provide).

## What this release test does NOT cover

- Cross-network / TURN - see `docs/testing/phase-3-cross-network-test.md` (blocked).
- Mobile/other-browser viewers - not exercised by this agent this phase (an Android phone was
  tested successfully by the project owner directly in an earlier session).
- Automatic (real-time) duration expiration - would require a real wait (15 min minimum) or a
  `TEST_DURATION_OVERRIDE_MS` change to the production signaling config, which was not made (see
  `docs/deployment/phase-2.md` - this variable must never be set in production).
- Long-duration (1 hour / 5 hours) sessions - only `FifteenMinutes` and `UntilStopped` were
  exercised this phase; the duration→expiry computation itself is unit-tested for all four values
  (`signaling/test/sessionDuration.test.js`, unchanged this phase).
