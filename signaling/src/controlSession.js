'use strict';

const crypto = require('crypto');

const ControlSessionStatus = Object.freeze({
    PENDING: 'PENDING',
    AUTHORIZED: 'AUTHORIZED',
    ACTIVE: 'ACTIVE',
    REVOKED: 'REVOKED',
    EXPIRED: 'EXPIRED',
});

const CONTROL_SESSION_TIMEOUT_MS = 5 * 60 * 1000; // 5 minutes to authorize
const CONTROL_SESSION_MAX_AGE_MS = 24 * 60 * 60 * 1000; // 24 hours max lifetime

function generatePairingCode() {
    const bytes = crypto.randomBytes(4);
    const num = bytes.readUInt32BE(0);
    return String(num).padStart(8, '0').replace(/(\d{4})(\d{4})/, '$1-$2');
}

function generateControlToken() {
    return crypto.randomBytes(32).toString('base64url');
}

function generateControlSessionId() {
    return crypto.randomBytes(16).toString('base64url');
}

class ControlSession {
    constructor(screenSessionId, hostToken) {
        this.id = generateControlSessionId();
        this.screenSessionId = screenSessionId;
        this.hostToken = hostToken;
        this.status = ControlSessionStatus.PENDING;
        this.pairingCode = generatePairingCode();
        this.controlToken = generateControlToken();
        this.createdAt = Date.now();
        this.authorizedAt = null;
        this.expiresAt = this.createdAt + CONTROL_SESSION_MAX_AGE_MS;
        this.authorizationTimeoutAt = this.createdAt + CONTROL_SESSION_TIMEOUT_MS;
        this.viewerSocket = null;
        this.viewerId = null;
        this.revoked = false;
        // The already-open role=viewer socket that asked for control (via the normal viewer's
        // "Remote Control" button), kept only to deliver the host's authorize/deny decision back
        // to it. Distinct from viewerSocket above, which is the (later, separate) role=control
        // socket that actually carries the WebRTC control connection once approved.
        this.requesterSocket = null;
    }

    isExpired() {
        return Date.now() > this.expiresAt;
    }

    isAuthorizationExpired() {
        return this.status === ControlSessionStatus.PENDING && Date.now() > this.authorizationTimeoutAt;
    }

    canActivate() {
        return this.status === ControlSessionStatus.AUTHORIZED && !this.isExpired();
    }

    authorize() {
        if (this.status !== ControlSessionStatus.PENDING) return false;
        if (this.isAuthorizationExpired()) return false;
        this.status = ControlSessionStatus.AUTHORIZED;
        this.authorizedAt = Date.now();
        return true;
    }

    activate(viewerSocket, viewerId) {
        if (!this.canActivate()) return false;
        this.status = ControlSessionStatus.ACTIVE;
        this.viewerSocket = viewerSocket;
        this.viewerId = viewerId;
        return true;
    }

    revoke() {
        this.revoked = true;
        this.status = ControlSessionStatus.REVOKED;
    }

    deactivate() {
        if (this.status === ControlSessionStatus.ACTIVE) {
            this.status = ControlSessionStatus.AUTHORIZED;
            this.viewerSocket = null;
            this.viewerId = null;
        }
    }

    toHostView() {
        return {
            id: this.id,
            status: this.status,
            pairingCode: this.pairingCode,
            createdAt: this.createdAt,
            authorizedAt: this.authorizedAt,
            expiresAt: this.expiresAt,
            isActive: this.status === ControlSessionStatus.ACTIVE,
        };
    }

    toViewerView() {
        return {
            id: this.id,
            status: this.status,
            pairingCode: this.pairingCode,
            controlToken: this.controlToken,
            screenSessionId: this.screenSessionId,
        };
    }
}

module.exports = {
    ControlSession,
    ControlSessionStatus,
    CONTROL_SESSION_TIMEOUT_MS,
    CONTROL_SESSION_MAX_AGE_MS,
    generatePairingCode,
    generateControlToken,
    generateControlSessionId,
};