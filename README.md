# Dekh Bhai

A lightweight remote **screen mirroring / screen casting** application for Windows. Start a cast
session, get a link, anyone opens it in a browser to watch the host laptop's screen live - no
install on the viewer's side. It is not a meeting-style screen-share tool: there is no call, no
participants list, just a live mirror of the host's screen and audio.

**Phase 1** (`docs/architecture/phase-1-technology-decision.md`) built the core casting engine: a
Windows desktop host that captures the screen and system audio natively and publishes them over
WebRTC to a plain browser viewer, plus an installable Windows build (`docs/development/packaging.md`).

**Phase 2** (`docs/architecture/phase-2-technology-decision.md`) turns that engine into the actual
product: a session model with four durations (15 min / 1 hour / 5 hours / Until I Stop) and
server-enforced expiration, unguessable public session links (`/v/<id>`) with a QR code,
production-capable signaling (WSS, STUN/TURN via coturn, rate limiting, message validation), a
redesigned minimal host UI and viewer UX with reconnect handling, and multi-viewer support. **The
Phase 2 code is implemented and covered by 28 signaling + 6 desktop unit tests, but it has not yet
been deployed to a real public domain or tested across different networks/mobile devices** - see
`docs/testing/test-plan.md` for exactly what's verified vs. still open, and
`docs/deployment/phase-2.md` for the deployment procedure (domain/HTTPS, coturn, environment
variables) needed to close those gaps.

**Live deployment status**: the viewer is deployed to Vercel at
https://viewer-theta-ashy.vercel.app, pointed at the signaling server deployed to Render at
https://dekh-bhai-signaling.onrender.com. The signaling protocol itself is confirmed working (a
real WebSocket connection returns a correct `session-created` response), **but the Render
deployment is currently unreliable - roughly a quarter to three-quarters of requests fail** with
Render's own "no-server" routing error, not an application bug. See `docs/deployment/phase-2.md`
§2 ("Known issue") for the measurements and next step (check Render's service logs). TURN is
**not yet deployed** - see §3.

```
Windows laptop (native capture)  --WebRTC-->  Browser viewer (any device, no install)
                                       ^
                                STUN / TURN (coturn)
                                       ^
                          Node signaling relay (SDP/ICE only, never media)
```

## Quick start (development)

```
cd signaling && npm install && npm start        # signaling relay + viewer host, http://localhost:8787
cd desktop && dotnet run --project src/DekhBhai.App   # host app
```

Click **START SHARING**, pick a duration, then **START**. Open the printed `/v/<id>` link (or scan
the QR code) in a browser. Full setup details (FFmpeg native libs, etc.) are in
`docs/development/setup.md`. To install a built copy of the host app instead of running from
source, see `docs/development/packaging.md`. To point a build at a real production deployment
instead of localhost, see `docs/deployment/phase-2.md`.

## Repository layout

| Path | What |
|---|---|
| `desktop/` | .NET 8 solution: WPF host app + the capture/audio/media/WebRTC engine (`DekhBhai.Core`) |
| `signaling/` | Node.js WebSocket signaling relay (session model, TURN credentials, rate limiting, SDP/ICE only - never media) + serves `viewer/` |
| `viewer/` | Plain HTML/JS/CSS browser viewer, no build step, no install |
| `docs/` | Architecture decisions, deployment, dev setup, test plan |
| `scripts/` | Convenience launch scripts |

## Documentation

- [`docs/architecture/phase-1-technology-decision.md`](docs/architecture/phase-1-technology-decision.md) -
  Phase 1 stack, why, rejected alternatives, and known limitations.
- [`docs/architecture/phase-2-technology-decision.md`](docs/architecture/phase-2-technology-decision.md) -
  Phase 2 session model, signaling, TURN, and security decisions, and what's still unverified.
- [`docs/deployment/phase-2.md`](docs/deployment/phase-2.md) - how to deploy signaling/viewer/coturn
  to a real domain, and the environment variables involved.
- [`docs/development/setup.md`](docs/development/setup.md) - environment setup and how to run everything.
- [`docs/testing/test-plan.md`](docs/testing/test-plan.md) - what's been verified and how to re-verify it.
