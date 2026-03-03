"use strict";
import Logger from './logger.js';

const APP_BASE_URL = "http://127.0.0.1:8597";
const POLL_INTERVAL_MS = 2000;          // Fast sync – 2 seconds (was ~5 s via alarms)
const RECONNECT_INTERVAL_MS = 5000;     // Back-off when disconnected
const FETCH_TIMEOUT_MS = 3000;          // Abort hung fetches quickly

export default class Connector {
    constructor(onMessage, onDisconnect) {
        this.logger = new Logger();
        this.onMessage = onMessage;
        this.onDisconnect = onDisconnect;
        this.connected = undefined;
        this._polling = false;
        this._pollTimer = null;
        this._nextPollDelay = null;
        this._pendingPost = false;
        this._postQueue = [];
        this._onAlarmHandler = () => {
            if (!this._polling && !this._pollTimer) this._schedulePoll(0);
        };
    }

    connect() {
        // Keep the alarm as a safety-net wake-up for Manifest V3 service-worker
        chrome.alarms.create("xdm-keepalive", { periodInMinutes: 1 });
        chrome.alarms.onAlarm.removeListener(this._onAlarmHandler);
        chrome.alarms.onAlarm.addListener(this._onAlarmHandler);
        this._schedulePoll(0);
    }

    /* ---- fast recursive polling loop ---- */

    _schedulePoll(delayMs) {
        if (this._pollTimer) return;    // a poll cycle is already queued or running
        this._pollTimer = setTimeout(() => this._poll(), delayMs);
    }

    _poll() {
        this._pollTimer = null;         // timer has fired; cleared before async work
        this._polling = true;           // stays true for the entire async chain
        this._fetchWithTimeout(APP_BASE_URL + "/sync")
            .then(res => {
                if (!res.ok) {
                    throw new Error(`Sync failed: ${res.status} ${res.statusText}`);
                }
                this.connected = true;
                return res.json();
            })
            .then(json => {
                this.onMessage(json);
                this._nextPollDelay = POLL_INTERVAL_MS;
            })
            .catch(() => {
                this._handleDisconnect();
                this._nextPollDelay = RECONNECT_INTERVAL_MS;
            })
            .finally(() => {
                this._polling = false;
                this._schedulePoll(this._nextPollDelay ?? RECONNECT_INTERVAL_MS);
            });
    }

    /* ---- helpers ---- */

    _fetchWithTimeout(url, opts = {}) {
        const controller = new AbortController();
        const id = setTimeout(() => controller.abort(), FETCH_TIMEOUT_MS);
        return fetch(url, { ...opts, signal: controller.signal })
            .finally(() => clearTimeout(id));
    }

    _handleDisconnect() {
        if (this.connected !== false) {
            this.connected = false;
            this.onDisconnect();
        }
    }

    disconnect() {
        this._handleDisconnect();
    }

    isConnected() {
        return this.connected;
    }

    postMessage(url, data) {
        this._postQueue.push({ url, data });
        if (!this._pendingPost) {
            this._processNextPost();
        }
    }

    _processNextPost() {
        if (this._postQueue.length === 0) {
            this._pendingPost = false;
            return;
        }
        this._pendingPost = true;
        const { url, data } = this._postQueue.shift();
        this._fetchWithTimeout(APP_BASE_URL + url, {
            method: "POST",
            body: JSON.stringify(data)
        })
            .then(res => {
                if (!res.ok) {
                    throw new Error(`POST ${url} failed: ${res.status} ${res.statusText}`);
                }
                this.connected = true;
                return res.json();
            })
            .then(json => {
                this.onMessage(json);
            })
            .catch(() => this._handleDisconnect())
            .finally(() => this._processNextPost());
    }

    launchApp() {
        // placeholder – could shell-launch xdm-app via protocol handler
    }
}