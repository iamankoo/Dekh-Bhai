'use strict';

/**
 * Deployment-time configuration for the viewer, kept as a single plain global rather than a
 * build step - the viewer is deliberately a no-build static page (see README.md), and this is
 * the one value that differs between "signaling serves this page itself" (local dev - see
 * signaling/src/server.js) and "this page is hosted separately from signaling" (e.g. the viewer
 * deployed to Vercel while signaling runs on its own long-lived host - see
 * docs/deployment/phase-2.md).
 *
 * Leave DEKHBHAI_SIGNALING_ORIGIN empty to talk to signaling on the SAME origin this page was
 * loaded from (the default - correct whenever signaling itself serves this page, as it does in
 * local development). Set it to signaling's own origin (e.g.
 * "https://dekhbhai-signaling.fly.dev") when this page is deployed somewhere that does not also
 * run signaling, such as Vercel. This is public configuration (a WebSocket endpoint, not a
 * secret) - see docs/architecture/phase-2-technology-decision.md.
 */
window.DEKHBHAI_SIGNALING_ORIGIN = 'https://dekh-bhai-signaling.onrender.com';
