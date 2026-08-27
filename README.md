# Dekh Bhai

A lightweight remote **screen mirroring / screen casting** application for Windows. Start a cast
session, get a link, anyone opens it in a browser to watch the host laptop's screen live - no
install on the viewer's side. It is not a meeting-style screen-share tool: there is no call, no
participants list, no chat, just a live mirror of the host's screen and system audio.

```
Dekh Bhai host (Windows)
  → Start Sharing → choose duration → capture + system audio start → app minimizes
  → unique Internet-accessible viewer URL + QR code are generated
  → another device opens/scans the URL → live video + audio streams over WebRTC
  → host can keep using the laptop normally
  → host stops sharing → session ends cleanly → "Sharing Stopped" / "Designed by Aniket" / "START AGAIN"
```

## How it works

```
Windows laptop (native capture)  --WebRTC-->  Browser viewer (any device, no install)
                                       ^
                                STUN / TURN (coturn - not yet deployed, see Known limitations)
                                       ^
                          Node signaling relay (SDP/ICE only, never media)
```

- **Host app** (`desktop/`): a WPF app that captures the screen via Windows Graphics Capture and
  system audio via WASAPI loopback, encodes VP8/Opus, and sends both directly to each viewer over
  WebRTC. The signaling relay never sees a video/audio byte.
- **Signaling** (`signaling/`): a small Node.js WebSocket relay that only exchanges session
  lifecycle messages and SDP/ICE - it creates sessions, issues STUN/TURN server lists, and relays
  offer/answer/ICE candidates between host and viewer.
- **Viewer** (`viewer/`): a plain HTML/JS/CSS page (no build step, no install) that connects,
  negotiates WebRTC, and plays the stream.

## Installing and starting a share

1. Download `DekhBhai-Setup.exe` (see "Building the installer" below to produce one) and
   double-click it. Windows will ask for permission to install Dekh Bhai (UAC) - approve it; the
   installer handles trusting the signing certificate and installing the app itself, no manual
   steps required. No environment variables, config files, or other software need installing -
   the app comes preconfigured to talk to the real production service.
2. Launch **Dekh Bhai** from the Start Menu.
3. Click **START SHARING**, choose a duration - **15 Minutes**, **1 Hour**, **5 Hours**, or
   **Until I Stop** - then **START**.
4. Dekh Bhai starts capturing your screen and system audio, minimizes itself, and shows a
   **share link** and a **QR code** - both encode the exact same viewer URL
   (`https://<viewer-domain>/v/<session-id>`).
5. Send the link (or have someone scan the QR code) - anyone who opens it sees your screen live
   in their browser, with a **Fullscreen** button and an elapsed-time indicator. No login, no
   install on their side.
6. Keep using your laptop normally - Dekh Bhai stays minimized and does not appear in the
   captured video (see "What the viewer never sees" below).
7. Click **STOP SHARING** (restore the window from the taskbar first, or find it minimized in the
   system tray/taskbar) to end the session. The link stops working immediately. **START AGAIN**
   starts a completely fresh session with a new id and link - nothing is reused.

For a **15 Minutes / 1 Hour / 5 Hours** session, sharing stops automatically when the duration
elapses, on both the host and every viewer, even if nobody clicks Stop.

## What the viewer never sees

Dekh Bhai's own UI - the share link, QR code, Stop button, elapsed timer - lives only inside the
Dekh Bhai window, never in the captured video. There is no watermark, no floating control, no
border drawn into the stream. The one visible thing that isn't Dekh Bhai's doing is a brief
yellow line Windows itself draws on the physical screen when any app starts a Graphics Capture
session (a system indicator, not part of the transmitted video - see
`docs/architecture/phase-1-technology-decision.md` for the direct investigation proving this).

## Supported devices

| | |
|---|---|
| **Host** | Windows 10 (build 19041+) or Windows 11, x64 only. See `docs/development/packaging.md` for the exact compatibility table. |
| **Viewer** | Any modern browser with WebRTC support. **Actually tested**: Chrome (desktop, including two simultaneous viewers on one session). An Android phone on the same Wi-Fi as the host has also been confirmed working (live video decoding) by direct manual test. Edge, Firefox, Safari, and iOS have not been tested - they should work (the viewer uses only standard `RTCPeerConnection`/media element APIs) but this is not yet claimed as verified. See `docs/testing/phase-3-release-test.md` and `docs/testing/phase-3-cross-network-test.md` for exact test coverage. |

## Known limitations

- **No TURN server is deployed.** Same-network connections work; a host or viewer behind a
  symmetric NAT on a genuinely different network from its peer will currently fail to connect.
  The code fully supports TURN (coturn, standard REST credential scheme) - only the server
  deployment itself is missing. See `docs/testing/phase-3-cross-network-test.md`.
- **Cross-network sharing has not been tested** (different Wi-Fi networks, mobile data, a second
  physical device on a different network) - see the same document for exactly what was and
  wasn't verified, and why.
- **The production signaling deployment (Render free tier) has a rough cold-start** after ~15
  minutes of no traffic - fully reliable once warm, but the first requests during wake-up can
  intermittently fail. Two mitigations are prepared but not yet activated - see
  `docs/deployment/phase-3.md`.
- **Single primary monitor only** - no monitor picker yet.
- **1080p only claimed, not upscaled** - streams at the host's native display resolution.

## Building from source (development)

```
cd signaling && npm install && npm start        # signaling relay + viewer host, http://localhost:8787
cd desktop && dotnet run --project src/DekhBhai.App   # host app
```

This runs entirely locally - no production account or deployment needed. Full environment setup
(FFmpeg native libs, Windows SDK, etc.) is in `docs/development/setup.md`. To point a locally
built app at a real production deployment instead of localhost, see `docs/deployment/phase-2.md`.

## Building the installer

```powershell
$env:DEKHBHAI_PFX_PASSWORD = "<your signing cert password>"
scripts\build-installer.ps1
```

Produces `dist\release\DekhBhai-Setup.exe` - the one file to hand someone. It builds the MSIX
(self-contained: bundles the .NET runtime and FFmpeg) and wraps it, plus the signing certificate,
into a single Inno Setup installer that handles certificate trust and installation itself - see
`docs/architecture/phase-3-technology-decision.md` ("Single-file installer") for why MSIX package
identity is preserved rather than replaced with a plain portable exe (it's required for Windows
Graphics Capture's border-suppression capability).

To build just the raw MSIX without the installer wrapper (e.g. for development):
`scripts\build-msix.ps1` → `dist\DekhBhai.msix`. Full detail on both, including generating your
own signing certificate, is in `docs/development/packaging.md`.

## How the production infrastructure works

Three independently deployed pieces, each documented in full under `docs/deployment/`:

1. **Viewer** - a static site (Vercel). No server-side code; it's the same `viewer/` files
   deployed as-is.
2. **Signaling** - a long-lived Node.js process (currently Render). Deliberately **not** deployed
   to a serverless/edge platform - see `docs/architecture/phase-2-technology-decision.md` for why
   Vercel's own WebSocket support doesn't fit this component's session-duration requirements.
3. **TURN** (coturn) - documented, not yet deployed - see "Known limitations" above.

## Repository layout

| Path | What |
|---|---|
| `desktop/` | .NET 8 solution: WPF host app + the capture/audio/media/WebRTC engine (`DekhBhai.Core`) |
| `signaling/` | Node.js WebSocket signaling relay (session model, TURN credentials, rate limiting, SDP/ICE only - never media) + serves `viewer/` |
| `viewer/` | Plain HTML/JS/CSS browser viewer, no build step, no install |
| `tests/desktop-e2e/` | Windows UI Automation harness that drives the real installed app end to end - see its own README |
| `docs/` | Architecture decisions, deployment, dev setup, test plans |
| `scripts/` | Convenience launch/build scripts |

## Documentation

- [`docs/architecture/phase-1-technology-decision.md`](docs/architecture/phase-1-technology-decision.md) -
  Phase 1 stack (capture/encode/WebRTC engine), why, rejected alternatives, known limitations.
- [`docs/architecture/phase-2-technology-decision.md`](docs/architecture/phase-2-technology-decision.md) -
  Phase 2 session model, signaling protocol, TURN, and the Vercel/signaling deployment split.
- [`docs/architecture/phase-3-technology-decision.md`](docs/architecture/phase-3-technology-decision.md) -
  Phase 3 hardening: Render reliability investigation, host reconnect/resume, bugs found and fixed.
- [`docs/deployment/phase-2.md`](docs/deployment/phase-2.md) - how to deploy signaling/viewer/coturn,
  and the environment variables involved.
- [`docs/deployment/phase-3.md`](docs/deployment/phase-3.md) - what changed operationally in Phase 3.
- [`docs/development/setup.md`](docs/development/setup.md) - environment setup and how to run everything.
- [`docs/development/packaging.md`](docs/development/packaging.md) - building and installing the MSIX.
- [`docs/testing/test-plan.md`](docs/testing/test-plan.md) - Phase 1/2 verification checklist.
- [`docs/testing/phase-3-release-test.md`](docs/testing/phase-3-release-test.md) - full installed-app
  production test cycle results.
- [`docs/testing/phase-3-cross-network-test.md`](docs/testing/phase-3-cross-network-test.md) - honest
  status of cross-network/TURN testing (currently blocked - see above).
