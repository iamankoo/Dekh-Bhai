# Phase 3 Deployment Notes

This supplements `docs/deployment/phase-2.md` (which still has the primary Vercel/Render/coturn
deployment procedures - not duplicated here) with what changed in Phase 3.

## New environment variable: HOST_RECONNECT_GRACE_MS

| Variable | Default | Purpose |
|---|---|---|
| `HOST_RECONNECT_GRACE_MS` | `20000` (20s) | How long a session survives its host's TCP connection dropping before being torn down - gives a transient network blip (or a Render cold-start hiccup) a chance to reconnect and resume via the new `resume-session` message. See `docs/architecture/phase-3-technology-decision.md`. |

No action needed on the existing Render deployment unless you want to change the default - it
applies automatically since it has a safe built-in default.

## Render free-tier reliability: recommended next step

Confirmed this phase (see the technology-decision doc): the deployed signaling service is 100%
reliable once warm, but a cold start (after ~15 minutes idle) has a rough multi-second transition
where some requests fail with `x-render-routing: no-server`. Two independent mitigations exist,
and neither has been applied to the live deployment yet - both are ready:

1. **`.github/workflows/keep-signaling-warm.yml`** (added this phase, **not yet pushed** - per
   the Phase 3 git policy, nothing was committed automatically). Pings `/health` every 10 minutes
   via GitHub Actions, keeping the free instance from ever going idle. Commit and push it to
   activate the schedule - it does nothing until it exists on the repository's default branch on
   GitHub.
2. **Upgrade the Render service off the free tier.** Not done automatically per the brief ("do
   not upgrade Render automatically") - this is a billing decision for the repository owner.
   Either mitigation independently resolves the cold-start issue; the GitHub Actions ping is free
   and can be applied immediately, while upgrading the plan is the more robust long-term fix if
   real user traffic volume ever makes a 10-minute polling gap insufficient (e.g. genuinely
   idle periods longer than 15 minutes would still let it go cold between pings' effective
   coverage... actually a 10-minute ping comfortably beats the 15-minute idle timeout, so this
   should keep it warm indefinitely as long as the workflow itself keeps running).

## Graceful shutdown

`server.js` now handles `SIGTERM`/`SIGINT` by notifying every active session's viewers and host
before closing (see the technology-decision doc for why). This has no deployment-side
configuration - it's automatic on any host that sends a standard termination signal before
killing the process, which includes Render's own redeploy/restart behavior.

## TURN deployment

Unchanged from `docs/deployment/phase-2.md` §3 - still not deployed, still blocked on
infrastructure access this environment doesn't have. See
`docs/testing/phase-3-cross-network-test.md` for the current status and what's needed to unblock
it.

## Desktop app: no new configuration

`DEKHBHAI_SIGNALING_WS_URL`/`DEKHBHAI_VIEWER_BASE_URL` are unchanged from Phase 2. The reconnect/
resume behavior is entirely internal to the signaling protocol and requires no new desktop-side
configuration.
