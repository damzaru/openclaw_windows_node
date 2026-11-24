// Modified by OpenClaw: temporary snapshot-identity backport; see OPENCLAW_PATCH_NOTICE.md.
/**
 * @license
 * Copyright 2025 Google LLC
 * SPDX-License-Identifier: Apache-2.0
 */
var __addDisposableResource = (this && this.__addDisposableResource) || function (env, value, async) {
    if (value !== null && value !== void 0) {
        if (typeof value !== "object" && typeof value !== "function") throw new TypeError("Object expected.");
        var dispose, inner;
        if (async) {
            if (!Symbol.asyncDispose) throw new TypeError("Symbol.asyncDispose is not defined.");
            dispose = value[Symbol.asyncDispose];
        }
        if (dispose === void 0) {
            if (!Symbol.dispose) throw new TypeError("Symbol.dispose is not defined.");
            dispose = value[Symbol.dispose];
            if (async) inner = dispose;
        }
        if (typeof dispose !== "function") throw new TypeError("Object not disposable.");
        if (inner) dispose = function() { try { inner.call(this); } catch (e) { return Promise.reject(e); } };
        env.stack.push({ value: value, dispose: dispose, async: async });
    }
    else if (async) {
        env.stack.push({ async: true });
    }
    return value;
};
var __disposeResources = (this && this.__disposeResources) || (function (SuppressedError) {
    return function (env) {
        function fail(e) {
            env.error = env.hasError ? new SuppressedError(e, env.error, "An error was suppressed during disposal.") : e;
            env.hasError = true;
        }
        var r, s = 0;
        function next() {
            while (r = env.stack.pop()) {
                try {
                    if (!r.async && s === 1) return s = 0, env.stack.push(r), Promise.resolve().then(next);
                    if (r.dispose) {
                        var result = r.dispose.call(r.value);
                        if (r.async) return s |= 2, Promise.resolve(result).then(next, function(e) { fail(e); return next(); });
                    }
                    else s |= 1;
                }
                catch (e) {
                    fail(e);
                }
            }
            if (s === 1) return env.hasError ? Promise.reject(env.error) : Promise.resolve();
            if (env.hasError) throw env.error;
        }
        return next();
    };
})(typeof SuppressedError === "function" ? SuppressedError : function (error, suppressed, message) {
    var e = new Error(message);
    return e.name = "SuppressedError", e.error = error, e.suppressed = suppressed, e;
});
import { CdpFrame } from './third_party/index.js';
import { logger } from './utils/logger.js';
import { withTimeout } from './utils/timeout.js';
const ACCESSIBILITY_SNAPSHOT_TIMEOUT_MS = 10_000;
/**
 * InlineTextBox nodes are layout line fragments of a StaticText node. They
 * repeat the parent text, have no backendNodeId and cannot be targeted by
 * tools. Puppeteer only serializes them for non-interesting-only snapshots.
 */
function isLayoutOnlyNode(node) {
    return node.role === 'InlineTextBox';
}
function stableNodeKey(node, sessionId) {
    if (!sessionId ||
        !node.loaderId ||
        !node.backendNodeId ||
        node.backendNodeId < 0) {
        return;
    }
    return `${sessionId}_${node.loaderId}_${node.backendNodeId}`;
}
export class TextSnapshot {
    static nextSnapshotId = 1;
    static resetCounter() {
        TextSnapshot.nextSnapshotId = 1;
    }
    root;
    idToNode;
    snapshotId;
    selectedElementUid;
    hasSelectedElement;
    verbose;
    #nodesByFrame;
    constructor(data) {
        this.root = data.root;
        this.idToNode = data.idToNode;
        this.snapshotId = data.snapshotId;
        this.selectedElementUid = data.selectedElementUid;
        this.hasSelectedElement = data.hasSelectedElement;
        this.verbose = data.verbose;
        this.#nodesByFrame = data.nodesByFrame;
    }
    static async create(page, options = {}) {
        const verbose = options.verbose ?? false;
        const mainFrame = page.pptrPage.mainFrame();
        const frameStates = new Map();
        for (const frame of page.pptrPage.frames()) {
            if (frame instanceof CdpFrame) {
                frameStates.set(frame, {
                    client: frame.client,
                    loaderId: frame._loaderId,
                });
            }
        }
        const currentClient = (frame) => {
            const state = frameStates.get(frame);
            if (!state ||
                !(frame instanceof CdpFrame) ||
                frame.detached ||
                frame.client !== state.client ||
                frame._loaderId !== state.loaderId) {
                throw new Error('Snapshot document changed. Take a new snapshot.');
            }
            return state.client;
        };
        const rootNode = await withTimeout(page.pptrPage.accessibility.snapshot({
            includeIframes: true,
            interestingOnly: !verbose,
        }), {
            timeout: ACCESSIBILITY_SNAPSHOT_TIMEOUT_MS,
            message: `Timed out after ${ACCESSIBILITY_SNAPSHOT_TIMEOUT_MS}ms waiting for the accessibility tree. The page may have a pending navigation or a crashed renderer. Navigate or reload the page, or select another page.`,
        });
        if (!rootNode) {
            throw new Error('Failed to create accessibility snapshot');
        }
        const { uniqueBackendNodeIdToMcpId } = page;
        const snapshotId = TextSnapshot.nextSnapshotId++;
        // Iterate through the whole accessibility node tree and assign node ids that
        // will be used for the tree serialization and mapping ids back to nodes.
        let idCounter = 0;
        const idToNode = new Map();
        const seenUniqueIds = new Set();
        const nodeFrames = new Map();
        const documentIds = new Map();
        const nodesByFrame = new Map();
        const keyCounts = new Map();
        const pending = [{ node: rootNode, frame: mainFrame }];
        for (const entry of pending) {
            const node = entry.node;
            let frame = entry.frame;
            if (node !== rootNode && node.role === 'RootWebArea') {
                const env_1 = { stack: [], error: void 0, hasError: false };
                try {
                    const handle = __addDisposableResource(env_1, await node.elementHandle(), false);
                    if (!handle) {
                        throw new Error('Snapshot document disappeared. Take a new snapshot.');
                    }
                    frame = handle.frame;
                }
                catch (e_1) {
                    env_1.error = e_1;
                    env_1.hasError = true;
                }
                finally {
                    __disposeResources(env_1);
                }
            }
            const key = stableNodeKey(node, currentClient(frame).id());
            if (key !== undefined) {
                keyCounts.set(key, (keyCounts.get(key) ?? 0) + 1);
            }
            if (node.role === 'RootWebArea') {
                documentIds.set(frame, node.loaderId);
            }
            nodeFrames.set(node, frame);
            for (const child of node.children ?? []) {
                pending.push({ node: child, frame });
            }
        }
        // A repeated backend identity cannot identify either occurrence across captures.
        for (const [key, count] of keyCounts) {
            if (count > 1) {
                uniqueBackendNodeIdToMcpId.delete(key);
            }
        }
        const assignIds = (node) => {
            const frame = nodeFrames.get(node);
            if (!frame) {
                throw new Error('Snapshot node has no frame');
            }
            const key = stableNodeKey(node, currentClient(frame).id());
            const stableKey = key !== undefined && keyCounts.get(key) === 1 ? key : undefined;
            const id = node.backendNodeId === undefined ? '' : (stableKey !== undefined
                ? uniqueBackendNodeIdToMcpId.get(stableKey)
                : undefined) ?? `${snapshotId}_${idCounter++}`;
            if (stableKey !== undefined) {
                uniqueBackendNodeIdToMcpId.set(stableKey, id);
                seenUniqueIds.add(stableKey);
            }
            const nodeWithId = {
                ...node,
                id,
                children: node.children
                    ? node.children
                        .filter(child => !isLayoutOnlyNode(child))
                        .map(child => assignIds(child))
                    : [],
                elementHandle: async () => {
                    currentClient(frame);
                    const handle = await node.elementHandle();
                    try {
                        currentClient(frame);
                        return handle;
                    }
                    catch (error) {
                        await handle?.dispose();
                        throw error;
                    }
                },
            };
            if (nodeWithId.backendNodeId) {
                let nodes = nodesByFrame.get(frame);
                if (!nodes) {
                    nodes = new Map();
                    nodesByFrame.set(frame, nodes);
                }
                nodes.set(nodeWithId.backendNodeId, nodeWithId);
            }
            // The AXNode for an option doesn't contain its `value`.
            // Therefore, set text content of the option as value.
            if (node.role === 'option') {
                const optionText = node.name;
                if (optionText) {
                    nodeWithId.value = optionText.toString();
                }
            }
            if (id) {
                idToNode.set(nodeWithId.id, nodeWithId);
            }
            return nodeWithId;
        };
        const rootNodeWithId = assignIds(rootNode);
        await TextSnapshot.#insertExtraNodes(page, idToNode, seenUniqueIds, snapshotId, idCounter, nodesByFrame, documentIds, currentClient, options.extraHandles ?? []);
        const snapshot = new TextSnapshot({
            root: rootNodeWithId,
            snapshotId: String(snapshotId),
            idToNode,
            hasSelectedElement: false,
            verbose,
            nodesByFrame,
        });
        const data = options.devtoolsData ?? (await page.getDevToolsData());
        if (data?.cdpBackendNodeId) {
            snapshot.hasSelectedElement = true;
            snapshot.selectedElementUid = snapshot.resolveCdpElementId(data.cdpBackendNodeId);
        }
        // Clean up unique IDs that we did not see anymore.
        for (const key of uniqueBackendNodeIdToMcpId.keys()) {
            if (!seenUniqueIds.has(key)) {
                uniqueBackendNodeIdToMcpId.delete(key);
            }
        }
        return snapshot;
    }
    resolveCdpElementId(cdpBackendNodeId, frame) {
        if (!cdpBackendNodeId) {
            logger?.('no cdpBackendNodeId');
            return;
        }
        if (frame) {
            return this.#nodesByFrame.get(frame)?.get(cdpBackendNodeId)?.id;
        }
        let match;
        const queue = [this.root];
        let current = queue.pop();
        while (current) {
            if (current.backendNodeId === cdpBackendNodeId) {
                if (match !== undefined) {
                    return;
                }
                match = current.id;
            }
            for (const child of current.children) {
                queue.push(child);
            }
            current = queue.pop();
        }
        return match;
    }
    // ExtraHandles represent DOM nodes which might not be part of the accessibility tree, e.g. DOM nodes
    // returned by third-party developer tools. We insert them into the tree by finding the closest ancestor
    // in the tree and inserting the node as a child. The ancestor's child nodes are re-parented if necessary.
    static async #insertExtraNodes(page, idToNode, seenUniqueIds, snapshotId, idCounter, nodesByFrame, documentIds, currentClient, extraHandles) {
        const { uniqueBackendNodeIdToMcpId } = page;
        const createExtraNode = async (handle) => {
            const env_2 = { stack: [], error: void 0, hasError: false };
            try {
                const frame = handle.frame;
                const client = currentClient(frame);
                const backendNodeId = await handle.backendNodeId();
                let nodes = nodesByFrame.get(frame);
                if (!backendNodeId || nodes?.has(backendNodeId)) {
                    return null;
                }
                const documentId = documentIds.get(frame);
                const uniqueBackendId = documentId
                    ? `custom_${client.id()}_${documentId}_${backendNodeId}`
                    : undefined;
                const id = (uniqueBackendId !== undefined
                    ? uniqueBackendNodeIdToMcpId.get(uniqueBackendId)
                    : undefined) ?? `${snapshotId}_${idCounter++}`;
                if (uniqueBackendId !== undefined) {
                    uniqueBackendNodeIdToMcpId.set(uniqueBackendId, id);
                    seenUniqueIds.add(uniqueBackendId);
                }
                const tagHandle = __addDisposableResource(env_2, await handle.getProperty('localName'), false);
                const tagValue = await tagHandle.jsonValue();
                const extraNode = {
                    role: tagValue,
                    id,
                    backendNodeId,
                    children: [],
                    elementHandle: async () => {
                        currentClient(frame);
                        return await handle.evaluateHandle(element => element);
                    },
                };
                if (!nodes) {
                    nodes = new Map();
                    nodesByFrame.set(frame, nodes);
                }
                nodes.set(backendNodeId, extraNode);
                return extraNode;
            }
            catch (e_2) {
                env_2.error = e_2;
                env_2.hasError = true;
            }
            finally {
                __disposeResources(env_2);
            }
        };
        const findAncestorNode = async (handle) => {
            const env_3 = { stack: [], error: void 0, hasError: false };
            try {
                let ancestorHandle = await handle.evaluateHandle(el => el.parentElement);
                const stack = __addDisposableResource(env_3, new DisposableStack(), false);
                while (ancestorHandle) {
                    stack.use(ancestorHandle);
                    const ancestorElement = ancestorHandle.asElement();
                    if (!ancestorElement) {
                        return null;
                    }
                    const ancestorBackendId = await ancestorElement.backendNodeId();
                    if (ancestorBackendId) {
                        const ancestorNode = nodesByFrame
                            .get(handle.frame)
                            ?.get(ancestorBackendId);
                        if (ancestorNode) {
                            return ancestorNode;
                        }
                    }
                    const nextHandle = await ancestorElement.evaluateHandle(el => el.parentElement);
                    ancestorHandle = nextHandle;
                }
                return null;
            }
            catch (e_3) {
                env_3.error = e_3;
                env_3.hasError = true;
            }
            finally {
                __disposeResources(env_3);
            }
        };
        const findDescendantNodes = async (frame, backendNodeId) => {
            const descendantIds = new Set();
            if (!backendNodeId) {
                return descendantIds;
            }
            try {
                const client = currentClient(frame);
                const { node } = await client.send('DOM.describeNode', {
                    backendNodeId,
                    depth: -1,
                    pierce: true,
                });
                const collect = (node) => {
                    if (node.backendNodeId && node.backendNodeId !== backendNodeId) {
                        descendantIds.add(node.backendNodeId);
                    }
                    if (node.children) {
                        for (const child of node.children) {
                            collect(child);
                        }
                    }
                };
                collect(node);
            }
            catch (e) {
                logger?.(`Failed to collect descendants for backend node ${backendNodeId}`, e);
            }
            return descendantIds;
        };
        const moveChildNodes = (attachTarget, extraNode, descendantIds) => {
            let firstMovedIndex = -1;
            if (descendantIds.size > 0 && attachTarget.children) {
                const remainingChildren = [];
                for (const child of attachTarget.children) {
                    if (child.backendNodeId && descendantIds.has(child.backendNodeId)) {
                        if (firstMovedIndex === -1) {
                            firstMovedIndex = remainingChildren.length;
                        }
                        extraNode.children.push(child);
                    }
                    else {
                        remainingChildren.push(child);
                    }
                }
                attachTarget.children = remainingChildren;
            }
            return firstMovedIndex !== -1
                ? firstMovedIndex
                : attachTarget.children
                    ? attachTarget.children.length
                    : 0;
        };
        if (extraHandles.length) {
            page.extraHandles = extraHandles;
        }
        const reorgInfo = [];
        for (const handle of page.extraHandles) {
            const extraNode = await createExtraNode(handle);
            if (!extraNode) {
                continue;
            }
            idToNode.set(extraNode.id, extraNode);
            const attachTarget = (await findAncestorNode(handle)) ??
                nodesByFrame
                    .get(handle.frame)
                    ?.values()
                    .find(node => node.role === 'RootWebArea');
            if (!attachTarget) {
                throw new Error('Extra node has no snapshot document. Take a new snapshot.');
            }
            const descendantIds = await findDescendantNodes(handle.frame, extraNode.backendNodeId);
            reorgInfo.push({ extraNode, attachTarget, descendantIds });
        }
        for (const { extraNode, attachTarget, descendantIds } of reorgInfo) {
            const index = moveChildNodes(attachTarget, extraNode, descendantIds);
            attachTarget.children.splice(index, 0, extraNode);
        }
    }
}
//# sourceMappingURL=TextSnapshot.js.map