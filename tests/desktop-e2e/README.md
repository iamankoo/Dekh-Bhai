# Desktop end-to-end test harness

Drives the **real installed Dekh Bhai MSIX application** (not `dotnet run`, not a mock, not a
simulated host) through the actual Start Sharing → Live → Stop Sharing flow using Windows UI
Automation, against the real production signaling/viewer deployment. This is separate from
`desktop/tests/DekhBhai.Core.Tests` (pure unit tests, no UI, no network) - this harness exercises
the shipped product end to end.

## What this is, and isn't

- **Host-side automation is a real, reusable PowerShell script** (`Invoke-DekhBhaiHostE2E.ps1` /
  `Invoke-DekhBhaiHostStop.ps1`) using `System.Windows.Automation` - no coordinate-based clicking
  anywhere, every interaction targets a stable `AutomationId`.
- **The viewer/browser side is not a scripted driver** (no Playwright/Selenium/WinAppDriver is
  installed in this environment, and installing a new browser-automation framework wasn't judged
  worth it for a verification task of this size). It was driven interactively via Claude's own
  Chrome browser tool during the actual test run recorded in the Phase 2 integration report, using
  the same `RTCPeerConnection.getStats()` calls documented below. If this needs to become a fully
  unattended single-command harness later, the natural next step is a small Playwright script that
  opens the captured share URL and runs the same `getStats()` polling loop shown in "Manual
  browser-side check" below.

## Why UI Automation via PowerShell, not WinAppDriver/FlaUI

`System.Windows.Automation` (`UIAutomationClient`/`UIAutomationTypes`) is already present on any
Windows machine and loads directly in Windows PowerShell 5.1 via `Add-Type -AssemblyName` - no
install step, no separate driver process to keep alive (WinAppDriver would need to be downloaded
and run as a background service; FlaUI would need adding as a NuGet dependency to a new .NET test
project). Given this only needs to drive one WPF window through a handful of buttons, the
zero-additional-dependency option was preferred.

## AutomationId strategy

**No application code changes were needed.** WPF's default `AutomationPeer` implementation
reports a `FrameworkElement`'s `x:Name` as its `AutomationId` unless
`AutomationProperties.AutomationId` is explicitly set - confirmed directly by inspecting the
running app's live automation tree before writing this script (`Invoke-ButtonById`/`Find-ById`
below query by `AutomationIdProperty`). Every control this harness touches already has an
`x:Name` in `desktop/src/DekhBhai.App/MainWindow.xaml`:

| AutomationId | XAML control |
|---|---|
| `StartSharingButton` | Idle screen's "START SHARING" button |
| `Duration15Button` / `Duration1hButton` / `Duration5hButton` / `DurationUntilStoppedButton` | duration picker buttons |
| `StartButton` | duration screen's "START" button |
| `DurationStatusText` | error/status text shown if session creation fails |
| `ShareUrlBox` | read-only textbox containing the generated share URL |
| `StopButton` | "STOP SHARING" button |
| `StartAgainButton` | post-session screen's "START AGAIN" button |

One caveat found while writing this: the screen containers themselves (`IdlePanel`,
`DurationPanel`, `LivePanel`, `PostSessionPanel` - plain `StackPanel`s with no
`AutomationProperties` set) are **not** exposed as nodes in UI Automation's Control view at all
(WPF's default automation peer for an unadorned layout `Panel` reports `IsControlElement=false`).
Don't try to `FindFirst` by one of those names - it will always return null. Instead, check
`AutomationElement.Current.IsOffscreen` on a control you know is inside the panel you care about
(e.g. `ShareUrlBox` to detect the Live screen, or search all `ControlType.Text` descendants and
filter by `IsOffscreen -eq $false` to read whichever screen's text is actually showing, as
`Invoke-DekhBhaiHostStop.ps1` does for the post-session texts).

## Prerequisites

- Windows 10 2004+ / Windows 11, x64.
- Windows PowerShell 5.1 (`powershell.exe`, not `pwsh`/PowerShell 7 - `UIAutomationClient`/
  `UIAutomationTypes` are full-.NET-Framework assemblies).
- Dekh Bhai already built and installed as MSIX - see `docs/development/packaging.md`. If not yet
  installed:
  ```powershell
  $env:DEKHBHAI_PFX_PASSWORD = '<the signing cert password>'
  .\scripts\build-msix.ps1
  Add-AppxPackage -Path .\dist\DekhBhai.msix
  ```
  If a package with the same version is already installed with different content (e.g. you
  rebuilt after a code change), `Add-AppxPackage` fails with `0x80073CFB` - remove the old one
  first: `Remove-AppxPackage -Package (Get-AppxPackage Aniket.DekhBhai).PackageFullName`.

## Running it

```powershell
cd tests\desktop-e2e

# 1. Launch the installed app, click through Start Sharing -> duration -> Start, capture the
#    share URL. Retries session creation up to -MaxSessionAttempts times if the production
#    signaling server (Render) is intermittently unavailable - see "Signaling flakiness" below.
#    Leaves the app running (Live, minimized) on success.
.\Invoke-DekhBhaiHostE2E.ps1
# -> prints the share URL to stdout, and logs/<runId>/report.json + run.log

# 2. Open the printed share URL in a browser and verify it - see "Manual browser-side check"
#    below for exactly what to check (this step is not scripted here - see "What this is, and
#    isn't" above).

# 3. Click Stop Sharing on the still-running instance and verify the post-session screen.
#    ALWAYS run this even if step 2 found a problem - it terminates the process so nothing is
#    left running in the background.
.\Invoke-DekhBhaiHostStop.ps1 -ProcessId <pid from step 1's output/log> -RunDir <logs\<runId> from step 1>
```

Optional parameters on `Invoke-DekhBhaiHostE2E.ps1`:
- `-SignalingWsUrl` / `-ViewerBaseUrl` - override the production defaults (e.g. to test against a
  local `signaling` dev server instead).
- `-Duration FifteenMinutes|OneHour|FiveHours|UntilStopped` - which duration button to click.
  Defaults to `FifteenMinutes` (shortest fixed option) so a run completes quickly; this harness
  does not wait for automatic expiration (a real 15-minute wait) - see "What isn't covered" below.
- `-MaxSessionAttempts` - retry budget for the signaling-availability retry loop.

## Manual browser-side check

Open the share URL in Chrome, wait a few seconds for `Live`, then in DevTools console (`pc` is
the viewer's module-scope `RTCPeerConnection` - see `viewer/viewer.js`):

```js
const stats = await pc.getStats();
const out = { connectionState: pc.connectionState, iceConnectionState: pc.iceConnectionState, video: null, audio: null };
stats.forEach(r => {
  if (r.type === 'inbound-rtp' && r.kind === 'video') out.video = { framesReceived: r.framesReceived, framesDecoded: r.framesDecoded, framesDropped: r.framesDropped, frameWidth: r.frameWidth, frameHeight: r.frameHeight, bytesReceived: r.bytesReceived };
  if (r.type === 'inbound-rtp' && r.kind === 'audio') out.audio = { packetsReceived: r.packetsReceived, bytesReceived: r.bytesReceived, totalSamplesReceived: r.totalSamplesReceived };
});
console.log(JSON.stringify(out, null, 2));
```

Run it twice, a few seconds apart, and confirm `framesReceived`/`framesDecoded`/`bytesReceived`
(video) and `totalSamplesReceived` (audio) are **increasing** between the two calls - a single
snapshot proves a connection exists, not that media is actually flowing. **A black picture is not
by itself a failure** - Windows Graphics Capture can legitimately return black frames (secure
desktop, protected content - see `docs/architecture/phase-1-technology-decision.md`). Only treat
it as a failure if `framesDecoded` stays at 0 or stops advancing.

## Signaling flakiness (Render)

The production signaling server (`docs/deployment/phase-2.md` §2, "Known issue") intermittently
returns `x-render-routing: no-server`. `Invoke-DekhBhaiHostE2E.ps1` retries clicking **START**
(not the whole app relaunch) up to `-MaxSessionAttempts` times, and records every attempt's
outcome (`live` / `error` / `timeout`) in `report.json`'s `signaling.attempts` array - this is
specifically so a failed run can be attributed to signaling availability rather than an app bug.
If every attempt fails, the script throws and the report's `errors` array plus each attempt's
`errorText` (read from the app's own `DurationStatusText`, which already translates connection
failures into a user-facing message - see `SessionController.TranslateError`) is the diagnostic
trail.

## What isn't covered here

- **Automatic expiration** - would require either a real 15-minute wait or setting
  `TEST_DURATION_OVERRIDE_MS` on the production signaling deployment (not done - modifying prod
  config for a test wasn't judged worth it; see `docs/testing/test-plan.md`). Only manual Stop is
  exercised.
- **TURN/cross-network connectivity** - this harness runs host and viewer on the same machine/
  network by construction (same laptop drives the app, and this session's Chrome browser). It
  cannot demonstrate NAT traversal - see `docs/deployment/phase-2.md` §3.
- **Fully unattended single-command run** - see "What this is, and isn't" above; the browser half
  is currently a documented manual/agent-driven procedure, not a script.

## Diagnostics on failure

Each run writes to `logs/<runId>/`:
- `run.log` - timestamped step-by-step log.
- `report.json` - structured result (app launch/state, every signaling attempt and its outcome,
  captured share URL, post-session text visibility).
- `NN-*.png` - screenshots of the Dekh Bhai window at key steps (idle, duration selected, and on
  failure). Only the app's own window region is captured (via `GetWindowRect` on its handle), not
  the full desktop, to avoid capturing unrelated content. No screenshot is possible while the
  window is minimized (expected once Live) - this is noted, not an error.

This directory's `logs/` output is gitignored - it's local test evidence, not something to commit.
