# Phase 2 Deployment Guide

This is the procedure for standing up a real, public Dekh Bhai deployment: the viewer web app,
the signaling server, and a TURN relay. **As of this revision, only the viewer's Vercel
configuration has actually been prepared and locally verified in this development environment -
the signaling server has not been deployed anywhere public, and no coturn instance exists.** No
domain, cloud account for signaling, or coturn instance was available to provision here. This
guide is written so the remaining steps can be followed and verified by whoever has that
infrastructure access; see `docs/testing/test-plan.md` for which end-to-end tests are still open
pending that deployment.

## Architecture recap

The viewer and signaling are **deployed separately** (a deliberate choice - see
`docs/architecture/phase-2-technology-decision.md` for why the signaling server was not moved to
Vercel):

```
Dekh Bhai.exe/MSIX  --WSS-->  Signaling server (Node.js, long-lived host: VM/Fly/Render/Railway)
        |                                |
        |                          SDP/ICE only, never media
        |                                |
        |                          serves iceServers (STUN/TURN)
        v                                v
   (share link / QR)  ------->  Viewer (static HTML/JS/CSS, deployed to Vercel)
                                        |
                        WebSocket connects cross-origin to the signaling server's own domain
                                        |
        +-------------------- WebRTC (direct) -------------------> Viewer browser
                                        |
                                 STUN (discovery) / TURN (relay fallback, separate infra)
```

Locally, signaling still serves the viewer itself (`express.static` in `signaling/src/server.js`)
so `npm start` + `dotnet run` keeps working exactly as before - nothing about local development
changed. In production, the viewer is a standalone static deployment that talks to signaling
cross-origin; `viewer/config.js` is the one file that tells it which origin to use (see below).

## 1. Deploying the viewer to Vercel

**Status: deployed and pointed at production signaling.** Live at
**https://viewer-theta-ashy.vercel.app** (Vercel project `iamankoos-projects/viewer`, root
directory `viewer/`). `viewer/config.js` now sets `DEKHBHAI_SIGNALING_ORIGIN` to
`https://dekh-bhai-signaling.onrender.com`, deployed and confirmed live via `curl`: the production
alias serves the updated `config.js` with that value. See "Known issue" under section 2 below -
the signaling deployment itself is currently unreliable, so a real session through this viewer
will intermittently fail to connect until that's resolved, independent of anything in the viewer
deployment.

### What changed to make this possible

The viewer was a single-origin page in Phase 1/early Phase 2 - `viewer.js` derived the signaling
WebSocket URL from `window.location.host`, which only works when signaling itself serves the
page. Three files were added/changed so the viewer can be hosted anywhere while still finding
signaling, with **no change to session ID/link format, QR/deep-link behavior, fullscreen, or any
of the live/stopped viewer states**:

- **`viewer/config.js`** (new) - a single plain global, `window.DEKHBHAI_SIGNALING_ORIGIN`,
  defaulting to `''` (empty = same origin as this page, i.e. today's behavior, unchanged). Set to
  signaling's real origin (e.g. `https://dekhbhai-signaling.fly.dev`) once signaling is deployed.
  Kept as a plain committed static file rather than a build-time env var substitution, consistent
  with the viewer's existing "no build step" design - this value is public configuration (a
  WebSocket endpoint, not a secret), same status as `DEKHBHAI_VIEWER_BASE_URL` on the desktop
  side.
- **`viewer/viewer.js`** - `wsUrl()` now goes through a new `signalingOrigin()` helper that reads
  `window.DEKHBHAI_SIGNALING_ORIGIN` when set (accepting either an `http(s)` or `ws(s)` origin),
  and falls back to the previous same-origin derivation when it isn't. No other logic changed -
  `getSessionId()` (path-based `/v/<id>` parsing), the six viewer states, the reconnect/backoff
  logic, the elapsed-time calculation, and the fullscreen button are all untouched.
- **`viewer/index.html`** - now loads `<script src="/config.js"></script>` immediately before
  `viewer.js`.
- **`viewer/vercel.json`** (new) - tells Vercel to serve `index.html` for any `/v/<sessionId>`
  path (a static host has no server-side route for that path the way `server.js`'s
  `app.get('/v/:sessionId', ...)` does) while leaving the actual browser URL as `/v/<sessionId>`
  intact, which `getSessionId()` depends on. Also sets two harmless static-response headers
  (`X-Content-Type-Options`, `Referrer-Policy`).

### What was verified (locally, in this session)

- `node --check` on the modified `viewer.js`/`config.js` - no syntax errors.
- Ran `signaling`'s own server locally and confirmed `GET /v/testSession123` still returns
  `index.html` with both `<script src="/config.js">` and `<script src="/viewer.js">` present, and
  that `GET /config.js` serves the new file correctly - i.e. the **local development path
  (signaling serving the viewer itself) is unaffected** by these changes.
- Did **not** verify an actual Vercel deployment (no account/CLI access here), a cross-origin
  WebSocket connection from a Vercel-hosted page to a real signaling deployment, or anything
  requiring a live signaling endpoint - see the verification checklist at the end of this
  document for exactly what's still open.

### Deploying it

From a machine with the Vercel CLI and an authenticated account:

```bash
npm i -g vercel        # one-time
cd viewer
vercel login           # one-time, opens a browser to authenticate
vercel link             # creates/links a Vercel project for this directory
vercel --prod           # deploys viewer/ as a static site
```

Or via the Vercel dashboard: **New Project → Import** this repository, and set the project's
**Root Directory** to `viewer` (important - otherwise Vercel will try to build the whole repo,
which is not a Vercel-shaped project). No build command or output directory override is needed -
it's plain static files.

Before the first real deploy, edit `viewer/config.js` and set
`window.DEKHBHAI_SIGNALING_ORIGIN` to signaling's actual deployed origin (once section 2 below is
done) - e.g. `'https://dekhbhai-signaling.fly.dev'`. Redeploy (`vercel --prod`) after any change
to this value; there is no runtime env var injection for a plain static site, so this is a
commit-and-redeploy step, not a dashboard toggle.

Vercel serves the deployed project over HTTPS automatically (including on the default
`*.vercel.app` domain), so `DEKHBHAI_VIEWER_BASE_URL` (set on the desktop host - see section 4)
should be the `https://` Vercel URL, e.g. `https://dekhbhai.vercel.app/`. A custom domain can be
attached in the Vercel project's Domains settings the same way as any other Vercel project.

## 2. Deploying the signaling server

**Status: not deployed - blocked on hosting access.** This machine has no account or CLI
credentials for any persistent-process host (Fly.io, Render, Railway, a VPS, AWS/GCP/Azure, etc.)
- unlike GitHub and Vercel, which were already authenticated here. Creating a new account on any
of these requires an interactive signup step (email verification, OAuth, or payment details) that
only the repository owner can complete. Once such an account/token exists, the steps below are
otherwise ready to run as-is. Deliberately kept as an unmodified, long-lived Node.js process rather
than ported to Vercel's WebSocket model - see
`docs/architecture/phase-2-technology-decision.md` for why (duration caps far shorter than the
product's 1-hour/5-hour/Until-I-Stop sessions, and no shared memory across function instances,
which the current `sessionStore.js` design relies on).

The signaling server is a plain Node.js process (`signaling/src/server.js`, entry point
`npm start`). Any Node 18+ host works - a small VM, a container, or a PaaS (Fly.io, Render,
Railway, etc. all run a Node process behind HTTPS/WSS with minimal setup).

### Render (chosen host for this deployment)

A Render Blueprint is checked in at the repo root (`render.yaml`) so this is a few clicks rather
than manual form-filling:

1. Go to the [Render dashboard](https://dashboard.render.com) → **New +** → **Blueprint**.
2. Connect the `iamankoo/Dekh-Bhai` GitHub repository (already pushed - see the repo root).
3. Render reads `render.yaml` and proposes one service, `dekh-bhai-signaling`, with
   `rootDir: signaling`, `npm ci --omit=dev` as the build command, `npm start` as the start
   command, and `/health` as the health check path - confirm and deploy.
4. Render assigns its own `PORT` automatically (the app already reads `process.env.PORT` with a
   `8787` fallback for local dev - `signaling/src/config.js` - so no change needed) and gives you
   an HTTPS/WSS-capable domain like `https://dekh-bhai-signaling.onrender.com` with **TLS already
   terminated** - no separate nginx/certbot step needed on Render specifically.
5. `TURN_URL`/`TURN_SECRET` are declared in `render.yaml` with `sync: false` - deliberately left
   for you to fill in via the Render dashboard's Environment tab once coturn (section 3) exists;
   they're never written into the blueprint file itself. Until then they're unset and the server
   correctly falls back to STUN-only (see `buildIceServers` in `turnCredentials.js`).
6. Once deployed, give the resulting domain back so `viewer/config.js` (Vercel) and the desktop
   app's `DEKHBHAI_SIGNALING_WS_URL`/`DEKHBHAI_VIEWER_BASE_URL` can be pointed at it and the
   viewer redeployed.

Free-tier Render web services spin down after a period of inactivity and take a few seconds to
wake on the next request - fine for testing, but worth upgrading to a paid instance type before
relying on this for a real "someone opens my link right now" demo, since the first WebSocket
connection after an idle period may time out waiting for the instance to wake.

### Known issue: this deployment is currently unreliable, not just cold-starting

**Update (Phase 3)**: a longer, sustained probe (76 continuous seconds against an already-warm
instance) came back 76/76 successful with consistent latency - this *is* ordinary cold-start
behavior, just with a rougher multi-second wake-up transition than a clean single delay, not an
ongoing crash loop. See `docs/architecture/phase-3-technology-decision.md` ("Render reliability
investigation") for the full re-investigation and `docs/deployment/phase-3.md` for the two
mitigations prepared (a keep-warm GitHub Actions ping, and upgrading off the free tier) -
neither has been applied to the live deployment yet.

Measured directly against `https://dekh-bhai-signaling.onrender.com` after deployment: repeated
`GET /health` requests, one per second for 20 seconds continuously, returned `200` only 14/20
times - failures were spread through the whole window, not clustered at the start the way a
one-time cold-start wake-up would be. Failing responses carry Render's own
`x-render-routing: no-server` header (not our Express app's 404 - it's Render's edge saying no
backend instance was available to route to at that instant), confirming this is a platform/
process-health issue, not an application bug. A matching WebSocket-level probe (8 real
`wss://.../ws?role=host` connection attempts, 1.5s apart) got a proper `session-created` response
- correct protocol, correct `sessionId`/`hostToken`/`iceServers` shape - on only **2 of 8**
attempts; the other 6 failed with `Unexpected server response: 404` at the WebSocket upgrade
step.

**Conclusion**: the signaling protocol implementation itself is confirmed correct end-to-end when
a request actually reaches the running instance (see the successful `session-created` payloads
above) - this is not a code defect in `server.js`. But at a ~25-70% request failure rate, this
deployment is not usable for a real session right now: a Windows host would frequently fail to
even create a session, and any session that did get created would be at real risk of the
WebSocket dropping mid-connection setup (offer/answer/ICE candidates flow over this same socket).
**This needs to be resolved (check Render's service Logs/Events tab for restart/crash entries,
and consider whether the free instance type is adequate) before any further real-session testing
is meaningful** - building a UI-driven end-to-end test on top of a backend failing this often
would produce misleading, non-reproducible results rather than a real verification.

```bash
cd signaling
npm ci --omit=dev
NODE_ENV=production PORT=8787 npm start
```

Run it under a process supervisor so it restarts on crash and on boot - `systemd`, `pm2`, or the
platform's own process manager. Example `systemd` unit:

```ini
[Unit]
Description=Dekh Bhai signaling
After=network.target

[Service]
WorkingDirectory=/opt/dekhbhai/signaling
ExecStart=/usr/bin/node src/server.js
Restart=always
EnvironmentFile=/opt/dekhbhai/signaling/.env
User=dekhbhai

[Install]
WantedBy=multi-user.target
```

`EnvironmentFile` should point at a `.env` (copied from `signaling/.env.example`, **never
committed**) with real production values - see "Environment variables" below.

Because sessions are in-memory (see `docs/architecture/phase-2-technology-decision.md`), a
restart/redeploy ends every in-flight session. There is no rolling-restart-without-disruption
story in Phase 2 - accept a brief outage window for deploys, or defer zero-downtime deploys to a
later phase once session state is externalized.

You need one domain (or subdomain) pointed at wherever the signaling server runs, e.g.
`dekhbhai-signaling.fly.dev` or `cast.example.com`:

- `wss://<signaling-domain>/ws` - signaling WebSocket (this is what `viewer/config.js` and
  `DEKHBHAI_SIGNALING_WS_URL` both need to point at).
- `https://<signaling-domain>/health`, `/ready` - health checks.

**Do not run production signaling over plain `ws://`/`http://`** - browsers increasingly block
mixed content (a Vercel-hosted, HTTPS-served viewer page in particular will refuse to open a
plain `ws://` connection at all), and SDP/ICE traffic should not be sent in the clear.

Terminate TLS with a reverse proxy in front of the Node process (nginx, Caddy, or your cloud
provider's load balancer) rather than terminating TLS inside `server.js` - this keeps certificate
renewal (e.g. Let's Encrypt/`certbot` or Caddy's automatic HTTPS) decoupled from an application
deploy. Example nginx `location` blocks:

```nginx
location /ws {
    proxy_pass http://127.0.0.1:8787;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
}

location / {
    proxy_pass http://127.0.0.1:8787;
    proxy_set_header Host $host;
}
```

Many PaaS providers (Fly.io, Render, Railway) terminate TLS and proxy WebSocket upgrades for you
with no nginx config needed - check the provider's own WebSocket support docs before adding a
manual reverse proxy in front of them.

## 3. TURN (coturn)

**Status: not deployed - this is the critical open item before any real cross-network test.**
STUN alone will not connect two peers behind symmetric NATs - a TURN relay is required for the
Phase 2 "different networks" guarantee. [coturn](https://github.com/coturn/coturn) is the
standard open-source choice and needs no custom code on Dekh Bhai's side; `turnCredentials.js`
already speaks its `use-auth-secret` REST API credential scheme.

**Install** (Debian/Ubuntu example):
```bash
sudo apt install coturn
```

**Minimal `/etc/turnserver.conf`:**
```
listening-port=3478
tls-listening-port=5349
fingerprint
lt-cred-mech
use-auth-secret
static-auth-secret=<same value as signaling's TURN_SECRET>
realm=cast.example.com
cert=/etc/letsencrypt/live/cast.example.com/fullchain.pem
pkey=/etc/letsencrypt/live/cast.example.com/privkey.pem
no-tcp-relay
```

**Ports** (open in the firewall/security group):
- `3478/udp` and `3478/tcp` - STUN/TURN control (plain).
- `5349/tcp` (and `/udp` if using DTLS-TURN) - TLS-secured TURN.
- A UDP relay range, e.g. `49152-65535/udp` (`min-port`/`max-port` in `turnserver.conf`) - this is
  where the actual relayed media flows; it must be reachable from the public Internet in both
  directions.

**TLS**: reuse the same certificate as the signaling domain (or a dedicated one for the TURN
realm) - coturn needs its own `cert`/`pkey` paths; a reverse proxy in front of signaling does not
cover this.

**Verify the shared secret matches**: `signaling`'s `TURN_SECRET` env var must be byte-for-byte
identical to coturn's `static-auth-secret` - a mismatch means coturn will reject every credential
Dekh Bhai issues, and this fails silently from the app's point of view (WebRTC just falls back to
direct/STUN-only connectivity and won't traverse a symmetric NAT). Test with coturn's own
`turnutils_uclient` or by inspecting `getStats()`'s `local-candidate`/`remote-candidate` pair type
(`relay` means TURN was actually used) on a real cross-network connection - this is the concrete
verification step still outstanding (see test-plan.md).

## 4. Environment variables

### Viewer (`viewer/config.js`)

| Value | Production setting |
|---|---|
| `window.DEKHBHAI_SIGNALING_ORIGIN` | signaling's public origin, e.g. `https://dekhbhai-signaling.fly.dev` |

Not an env var (see "Deploying the viewer to Vercel" above for why) - edit the file and redeploy.

### Signaling (`signaling/.env`, see `signaling/.env.example`)

| Variable | Production value | Notes |
|---|---|---|
| `NODE_ENV` | `production` | |
| `PORT` | e.g. `8787` | whatever your reverse proxy forwards to |
| `STUN_URLS` | `stun:stun.l.google.com:19302` (or your own) | comma-separated |
| `TURN_URL` | `turn:turn.example.com:3478` (or `turns:` for TLS) | your coturn host |
| `TURN_SECRET` | a long random value | **secret** - must match coturn's `static-auth-secret` exactly; never commit |
| `TURN_CREDENTIAL_TTL_SECONDS` | `3600` (default) | how long a minted TURN credential is valid |
| `MAX_VIEWERS_PER_SESSION` | `25` (default) | raise/lower per capacity planning |
| `HOST_HEARTBEAT_INTERVAL_MS` / `_TIMEOUT_MS` | `15000` / `45000` (defaults) | how fast a crashed/disconnected host is detected |
| `HOST_RECONNECT_GRACE_MS` | `20000` (default) | how long a session survives its host's TCP connection dropping before being ended - added in Phase 3, see `docs/deployment/phase-3.md` |
| `CLEANUP_INTERVAL_MS` | `10000` (default) | expiry/cleanup tick frequency |
| `RATE_LIMIT_WINDOW_MS` / `_MAX_MESSAGES` | `1000` / `50` (defaults) | per-connection signaling rate limit |
| `LOG_LEVEL` | `info` | `debug` for troubleshooting only - avoid in steady-state production (verbosity) |

Never set `TEST_DURATION_OVERRIDE_MS` in production - it exists solely so an automated test can
shorten a 15-minute/1-hour/5-hour session without actually waiting.

### Desktop host app

Set on the machine running Dekh Bhai (or embed at install time - see "Configuring an installed
MSIX build" below):

| Variable | Production value |
|---|---|
| `DEKHBHAI_SIGNALING_WS_URL` | `wss://dekh-bhai-signaling.onrender.com/ws?role=host` |
| `DEKHBHAI_VIEWER_BASE_URL` | `https://viewer-theta-ashy.vercel.app/` |

Both default to `ws://localhost:8787/...`/`http://localhost:8787/` (`AppConfig.cs`) when unset -
correct for local development, **wrong for any real install** (see "Current known MSIX issue"
below). Note these two now point at **two different domains** (the Vercel viewer and the
separately-hosted signaling server) - that's expected with this split deployment, and is exactly
what the two independent env vars were designed for.

## 5. Configuring an installed MSIX build

`Environment.GetEnvironmentVariable` (used by `AppConfig.cs`) reads whatever environment block the
process was launched with. For a normal Win32/MSIX full-trust app (this package declares
`runFullTrust`), that means:

- **Per-machine**, so every user/session picks it up without needing a fresh login for the
  *current* session to matter only for processes started after the change:
  ```powershell
  [Environment]::SetEnvironmentVariable("DEKHBHAI_SIGNALING_WS_URL", "wss://<signaling-domain>/ws?role=host", "Machine")
  [Environment]::SetEnvironmentVariable("DEKHBHAI_VIEWER_BASE_URL", "https://<vercel-viewer-domain>/", "Machine")
  ```
- A **sign-out/sign-in (or reboot)** is required before Explorer (and anything launched from the
  Start Menu tile, including Dekh Bhai) picks up a newly-set machine/user environment variable -
  Explorer's own environment block is captured at logon and does not refresh live. Setting the
  variable and then immediately launching from the Start Menu **will not** pick up the new value
  in the same session.
- These are **public configuration** (a WSS URL and an HTTPS base URL, not secrets), so embedding
  them is fine per the brief ("if configuration is public ... it may be embedded or configured
  appropriately"). **Never** set a TURN secret this way or bundle it into the MSIX - TURN
  credentials are minted server-side per-connection specifically so no long-lived secret needs to
  ship to the client at all (see the technology-decision doc).
- A cleaner Phase 3 alternative (not built): bake the production URLs in as compiled-in defaults
  for a "production" build configuration of `DekhBhai.App.csproj`, so a production MSIX needs no
  environment variable at all and there's nothing for an end user's machine to misconfigure. Not
  done in Phase 2 since the brief asks for configuration via environment, not per-build constants,
  and doing both would be two mechanisms for the same thing.

### Current known MSIX issue

A reported installed-build failure: `"failed to start: Unable to connect to the remote server"`.
Root cause, confirmed by reading the code path: an MSIX build's `AppConfig.SignalingWsUrl`
defaults to `ws://localhost:8787/ws?role=host` (`desktop/src/DekhBhai.App/AppConfig.cs`) when no
`DEKHBHAI_SIGNALING_WS_URL` is set - so on any machine that isn't also running a local `signaling`
dev server, the connection attempt fails with exactly that .NET `WebSocketException` message. This
is expected behavior for an unconfigured install, not a bug in the connection code - **the
installed build was never pointed at a real signaling endpoint**, because no such public endpoint
has been deployed yet (see section 2 above).

Two things are already true in the code, and were verified by reading
`SessionController.TranslateError` (`desktop/src/DekhBhai.Core/Session/SessionController.cs`):
a connect failure matching "unable to connect"/"refused"/"no such host"/"timed out" is translated
to *"Unable to connect to Dekh Bhai's signaling service. Check your Internet connection and try
again."* before being shown in the UI - the raw `WebSocketException` text should not actually
reach the end user. **This translation has not been re-confirmed against a real installed MSIX
build in this session** (doing so requires building+installing the package and triggering the
failure, which was not exercised here) - re-verify this specific behavior as part of the next
MSIX install test in `docs/testing/test-plan.md`.

The actual fix for the underlying issue is deployment, not code: once sections 1-3 above are done,
set `DEKHBHAI_SIGNALING_WS_URL`/`DEKHBHAI_VIEWER_BASE_URL` (as described above) on any machine
Dekh Bhai is installed on, pointing at the real production endpoints. **Do not** ship a production
MSIX that silently depends on a developer's local `signaling` process being open.

## 6. Health checks

`GET /health` returns `{ status, uptimeSeconds, activeSessions }`; `GET /ready` returns
`{ ready: true }`; `GET /healthz` is kept for Phase 1 script compatibility
(`scripts/dev-all.ps1` polls it). Point your process supervisor's/load balancer's health check at
`/health` or `/ready` - neither exposes secrets or infrastructure details beyond an uptime counter
and an active-session count. The viewer, on Vercel, has no equivalent - Vercel's own platform
health/uptime monitoring covers a static deployment.

## 7. Build/run command reference

```bash
# Viewer, production (once a Vercel account/CLI is available)
npm i -g vercel
cd viewer
vercel login
vercel link
vercel --prod

# Viewer, local development (unchanged - signaling serves it directly)
cd signaling && npm install && npm start   # viewer is available at http://localhost:8787/

# Signaling, production
cd signaling
npm ci --omit=dev
NODE_ENV=production npm start

# Signaling, local development (unchanged)
cd signaling
npm install
npm start

# Desktop host app, local development
cd desktop
dotnet run --project src/DekhBhai.App

# Desktop host app, MSIX production build (see docs/development/packaging.md for full detail)
scripts\build-msix.ps1
```

## Post-deployment verification checklist

Once the viewer is actually deployed to Vercel and signaling is deployed per section 2, verify
in this order (do not claim any later item works until the ones before it are confirmed):

1. [x] Vercel viewer URL loads over HTTPS - confirmed via `curl`:
       https://viewer-theta-ashy.vercel.app/ returns `200`, and `/v/testSession123` correctly
       rewrites to `index.html` with the path intact.
2. [x] `viewer/config.js` on the deployed site has `DEKHBHAI_SIGNALING_ORIGIN` set to the real
       signaling domain - confirmed via `curl https://viewer-theta-ashy.vercel.app/config.js`.
3. [x] Signaling accepts the existing WebSocket protocol correctly when reachable - a real
       `wss://dekh-bhai-signaling.onrender.com/ws?role=host` connection received a well-formed
       `session-created` response (`sessionId`, `hostToken`, `iceServers`) on the attempts that
       connected. **However only 2 of 8 attempts connected at all** - see "Known issue" in
       section 2 above. Items 4 onward below (real session, video, audio, termination) are
       blocked on that being fixed first - attempting them against a backend failing this often
       would not produce a trustworthy result.
4. [ ] A real Dekh Bhai session (desktop app pointed at the deployed signaling URL via
       `DEKHBHAI_SIGNALING_WS_URL`) generates a share URL pointing at the Vercel domain
       (`DEKHBHAI_VIEWER_BASE_URL`), and the QR code encodes the identical URL.
5. [ ] That URL opens successfully from a **separate** browser/device (not the host machine).
6. [ ] WebRTC negotiation succeeds - viewer reaches `connectionState: connected`
       (`RTCPeerConnection.getStats()`), not just the signaling WebSocket connecting.
7. [ ] Video frames are received (`framesDecoded` growing, `framesDropped: 0` via `getStats()`).
8. [ ] System audio is received (`bytesReceived` growing on the audio `inbound-rtp` stat).
9. [ ] Fullscreen works in the viewer and Esc does not end the session.
10. [ ] Stop Sharing / session expiration correctly ends the session and the viewer shows the
        matching terminal state.
11. [ ] No `localhost` URL appears anywhere in the deployed viewer's configuration or in the
        share link/QR code generated by a production-configured desktop build.

**Do not claim a real Internet end-to-end connection is complete until items 1-11 above are
individually confirmed, and do not claim cross-network/TURN reliability until section 3 (coturn)
is deployed and a connection has been forced through it from two genuinely different networks** -
none of that has happened yet in this environment. **Item 3's finding also means the Render
deployment's reliability itself needs to be fixed before items 4-11 are attempted**, since testing
a real session against a backend that fails 25-70%+ of requests would not produce a meaningful
result either way.

## What is NOT covered by this document

Actually provisioning a Vercel account/project, a signaling VM/container host, DNS records, a TLS
certificate, or a coturn instance - those require real cloud/hosting access and credentials that
this development environment does not have. This document describes each procedure precisely
enough to execute once that access exists; it does not claim any of it (beyond the viewer's local
configuration checks noted above) has been run against a live public domain. See
`docs/testing/test-plan.md` for the specific end-to-end tests that remain open pending that
deployment.
