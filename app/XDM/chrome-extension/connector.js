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
        this._pendingPost = false;
    }

    connect() {
        // Keep the alarm as a safety-net wake-up for Manifest V3 service-worker
        chrome.alarms.create("xdm-keepalive", { periodInMinutes: 1 });
        chrome.alarms.onAlarm.addListener(() => {
            if (!this._polling) this._schedulePoll(0);
        });
        this._schedulePoll(0);
    }

    /* ---- fast recursive polling loop ---- */

    _schedulePoll(delayMs) {
        if (this._polling) return;
        this._polling = true;
        setTimeout(() => this._poll(), delayMs);
    }

    _poll() {
        this._polling = false;
        this._fetchWithTimeout(APP_BASE_URL + "/sync")
            .then(res => {
                this.connected = true;
                return res.json();
            })
            .then(json => {
                this.onMessage(json);
                this._schedulePoll(POLL_INTERVAL_MS);
            })
            .catch(() => {
                this._handleDisconnect();
                this._schedulePoll(RECONNECT_INTERVAL_MS);
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
        if (this._pendingPost) {
            this.logger.log("Queuing post – previous still in-flight");
        }
        this._pendingPost = true;
        this._fetchWithTimeout(APP_BASE_URL + url, {
            method: "POST",
            body: JSON.stringify(data)
        })
            .then(res => {
                this.connected = true;
                return res.json();
            })
            .then(json => {
                this.onMessage(json);
            })
            .catch(() => this._handleDisconnect())
            .finally(() => { this._pendingPost = false; });
    }

    launchApp() {
        // placeholder – could shell-launch xdm-app via protocol handler
    }
}