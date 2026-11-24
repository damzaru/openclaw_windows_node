// The companion's target-bound adapter for the pinned Chrome DevTools MCP.
import { TextSnapshot } from './chrome-devtools-mcp/build/src/TextSnapshot.js';

const maxSide = 2000, maxBytes = 5 * 1024 * 1024, maxLabels = 100;

async function readBox(mcpPage, uid) {
    const handle = await mcpPage.getElementByUid(uid);
    try {
        // Puppeteer returns coordinates relative to the main frame, including for iframe refs.
        return await handle.boundingBox();
    } finally {
        await handle.dispose();
    }
}

// Runs in Puppeteer's isolated world. No overlays, scripts or image data are added to the DOM.
async function drawLabels(data, mime, labels, clip) {
    const bytes = Uint8Array.from(atob(data), c => c.charCodeAt(0));
    const bitmap = await createImageBitmap(new Blob([bytes], { type: mime }));
    const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
    const ctx = canvas.getContext('2d');
    ctx.drawImage(bitmap, 0, 0);
    bitmap.close();
    ctx.font = 'bold 12px monospace';
    ctx.textBaseline = 'top';
    for (const { uid, x, y } of labels) {
        const w = Math.ceil(ctx.measureText(uid).width) + 6;
        const left = Math.max(0, Math.min(canvas.width - w, (x - clip.x) * canvas.width / clip.width));
        const top = Math.max(0, Math.min(canvas.height - 16, (y - clip.y) * canvas.height / clip.height));
        ctx.fillStyle = '#a62c00';
        ctx.fillRect(left, top, w, 16);
        ctx.fillStyle = '#fff';
        ctx.fillText(uid, left + 3, top + 2);
    }
    const blob = await canvas.convertToBlob({ type: mime, quality: 0.85 });
    return await new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result.split(',')[1]);
        reader.onerror = () => reject(reader.error);
        reader.readAsDataURL(blob);
    });
}

export async function captureCompanionTab(mcpPage, { companionTarget, uid, labels, fullPage, format }, response) {
    if (mcpPage.targetId !== companionTarget) throw new Error('Browser target changed before screenshot.');
    const page = mcpPage.pptrPage;
    const session = await page.createCDPSession();
    const send = (method, params = {}) => session.send(method, params, { timeout: 20_000 });
    const documentId = async () => (await send('Page.getFrameTree')).frameTree.frame.loaderId;
    try {
        const original = await documentId();
        // Keep the hidden tab's compositor producing frames without activating the tab or window.
        session.on('Page.screencastFrame', ({ sessionId }) => {
            void send('Page.screencastFrameAck', { sessionId }).catch(() => {});
        });
        await send('Page.startScreencast', { format: 'jpeg', quality: 1, maxWidth: 1, maxHeight: 1 });
        const { cssVisualViewport: view, cssContentSize: content } = await send('Page.getLayoutMetrics');
        const box = uid ? await readBox(mcpPage, uid) : null;
        if (uid && (!box || box.width <= 0 || box.height <= 0)) throw new Error('Snapshot ref has no visible bounds.');
        const clip = box ? { ...box, x: box.x + view.pageX, y: box.y + view.pageY }
            : fullPage ? content : { x: view.pageX, y: view.pageY, width: view.clientWidth, height: view.clientHeight };
        if (!(clip.width > 0 && clip.height > 0)) throw new Error('Screenshot area is empty.');
        const realm = page.mainFrame().isolatedRealm();
        const dpr = await realm.evaluate(() => window.devicePixelRatio);
        let scale = Math.min(1, maxSide / (Math.max(clip.width, clip.height) * dpr));
        const marks = [];
        let skipped = 0, truncated = false;
        if (labels) {
            let refs = [uid];
            if (!uid) {
                mcpPage.textSnapshot = await TextSnapshot.create(mcpPage);
                refs = [];
                for (const node of mcpPage.textSnapshot.idToNode.values()) {
                    if (['StaticText', 'InlineTextBox', 'RootWebArea'].includes(node.role)) continue;
                    if (refs.length === maxLabels) { truncated = true; break; }
                    refs.push(node.id);
                }
            }
            for (const ref of refs) {
                const b = box ?? await readBox(mcpPage, ref).catch(() => null);
                if (!b || b.width <= 0 || b.height <= 0) { skipped++; continue; }
                const x = b.x + view.pageX, y = b.y + view.pageY;
                if (x + b.width <= clip.x || y + b.height <= clip.y ||
                    x >= clip.x + clip.width || y >= clip.y + clip.height) { skipped++; continue; }
                marks.push({ uid: ref, x, y });
            }
        }
        const mime = 'image/' + format;
        for (let attempt = 0; attempt < 3; attempt++, scale *= 0.7) {
            let { data } = await send('Page.captureScreenshot', {
                format, fromSurface: true, captureBeyondViewport: true, clip: { ...clip, scale },
                ...(format === 'jpeg' ? { quality: 85 } : {}),
            });
            if (marks.length) data = await realm.evaluate(drawLabels, data, mime, marks, clip);
            if (Buffer.byteLength(data, 'base64') > maxBytes) continue;
            if (await documentId() !== original) throw new Error('Tab navigated during screenshot; retry with a new snapshot.');
            response.appendResponseLine(JSON.stringify({ url: page.url(), ...(labels ? { labelsCount: marks.length, labelsSkipped: skipped, truncated } : {}) }));
            response.attachImage({ mimeType: mime, data });
            return;
        }
        throw new Error('Screenshot exceeds the Gateway image size limit.');
    } finally {
        try { await session.send('Page.stopScreencast', {}, { timeout: 1000 }); }
        finally { await session.detach().catch(() => {}); }
    }
}
