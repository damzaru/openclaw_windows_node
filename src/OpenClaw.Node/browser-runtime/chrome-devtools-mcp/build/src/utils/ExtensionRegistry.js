/**
 * @license
 * Copyright 2026 Google LLC
 * SPDX-License-Identifier: Apache-2.0
 */
import fs from 'node:fs/promises';
import path from 'node:path';
export class ExtensionRegistry {
    #extensions = new Map();
    async registerExtension(id, extensionPath) {
        const manifestPath = path.join(extensionPath, 'manifest.json');
        const manifestContent = await fs.readFile(manifestPath, 'utf-8');
        const manifest = JSON.parse(manifestContent);
        const name = manifest.name ?? 'Unknown';
        const version = manifest.version ?? 'Unknown';
        const extension = {
            id,
            name,
            version,
            isEnabled: true,
            path: extensionPath,
        };
        this.#extensions.set(extension.id, extension);
        return extension;
    }
    remove(id) {
        this.#extensions.delete(id);
    }
    list() {
        return Array.from(this.#extensions.values());
    }
    getById(id) {
        return this.#extensions.get(id);
    }
}
