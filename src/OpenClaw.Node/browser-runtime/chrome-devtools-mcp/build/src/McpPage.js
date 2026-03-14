/**
 * @license
 * Copyright 2025 Google LLC
 * SPDX-License-Identifier: Apache-2.0
 */
import { takeSnapshot } from './tools/snapshot.js';
/**
 * Per-page state wrapper. Consolidates dialog, snapshot, emulation,
 * and metadata that were previously scattered across Maps in McpContext.
 *
 * Internal class consumed only by McpContext. Fields are public for direct
 * read/write access. The dialog field is private because it requires an
 * event listener lifecycle managed by the constructor/dispose pair.
 */
export class McpPage {
    pptrPage;
    id;
    // Snapshot
    textSnapshot = null;
    uniqueBackendNodeIdToMcpId = new Map();
    // Emulation
    emulationSettings = {};
    // Metadata
    isolatedContextName;
    devToolsPage;
    // Dialog
    #dialog;
    #dialogHandler;
    constructor(page, id) {
        this.pptrPage = page;
        this.id = id;
        this.#dialogHandler = (dialog) => {
            this.#dialog = dialog;
        };
        page.on('dialog', this.#dialogHandler);
    }
    get dialog() {
        return this.#dialog;
    }
    getDialog() {
        return this.dialog;
    }
    clearDialog() {
        this.#dialog = undefined;
    }
    get networkConditions() {
        return this.emulationSettings.networkConditions ?? null;
    }
    get cpuThrottlingRate() {
        return this.emulationSettings.cpuThrottlingRate ?? 1;
    }
    get geolocation() {
        return this.emulationSettings.geolocation ?? null;
    }
    get viewport() {
        return this.emulationSettings.viewport ?? null;
    }
    get userAgent() {
        return this.emulationSettings.userAgent ?? null;
    }
    get colorScheme() {
        return this.emulationSettings.colorScheme ?? null;
    }
    dispose() {
        this.pptrPage.off('dialog', this.#dialogHandler);
    }
    async getElementByUid(uid) {
        if (!this.textSnapshot) {
            throw new Error(`No snapshot found for page ${this.id ?? '?'}. Use ${takeSnapshot.name} to capture one.`);
        }
        const node = this.textSnapshot.idToNode.get(uid);
        if (!node) {
            throw new Error(`Element uid "${uid}" not found on page ${this.id}.`);
        }
        return this.#resolveElementHandle(node, uid);
    }
    async #resolveElementHandle(node, uid) {
        const message = `Element with uid ${uid} no longer exists on the page.`;
        try {
            const handle = await node.elementHandle();
            if (!handle) {
                throw new Error(message);
            }
            return handle;
        }
        catch (error) {
            throw new Error(message, {
                cause: error,
            });
        }
    }
    getAXNodeByUid(uid) {
        return this.textSnapshot?.idToNode.get(uid);
    }
}
