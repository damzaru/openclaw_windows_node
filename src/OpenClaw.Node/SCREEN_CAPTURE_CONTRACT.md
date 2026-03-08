# OpenClaw Windows Node — `screen.capture` Contract

This document defines the **clean** `screen.capture` API for the Windows node.

## Goals

- File/metadata first (no accidental huge inline payloads)
- Single capture pipeline with explicit output sinks
- Strict params (no legacy aliases)
- Deterministic response envelope per mode

## Command

- `screen.capture`

## Request

```json
{
  "mode": "deliver | file | data",
  "screenIndex": 0,
  "windowHandle": 0,
  "format": "png | jpg | jpeg",
  "maxWidth": 1600,
  "quality": 0.85,
  "message": "Desktop screenshot",
  "sessionKey": "agent:main:...",
  "channel": "discord",
  "to": "channel:1475224687599554842",
  "outputPath": "C:\\path\\to\\shot.png",
  "maxInlineBytes": 1500000
}
```

### Required by mode

- `mode=deliver`
  - requires: `channel`, `to`
  - requires gateway RPC client
- `mode=file`
  - requires: `outputPath`
- `mode=data`
  - optional: `maxInlineBytes` (defaults to `1_500_000`)

### Validation

- `mode` must be one of: `deliver`, `file`, `data`
- `screenIndex >= 0`
- `format` must be one of: `png`, `jpg`, `jpeg`
- `quality` in `(0, 1]`
- `maxWidth > 0`
- `maxInlineBytes > 0`

## Legacy parameters (rejected)

These are intentionally unsupported and return `InvalidRequest`:

- `path`
- `handle`
- `route`
- `sendToAgent`
- `deliver`

## Response envelopes

All successful responses include:

- `ok: true`
- `mode`
- `target`
- `capture`

### `mode=deliver`

```json
{
  "ok": true,
  "mode": "deliver",
  "target": {
    "source": "screen",
    "screenIndex": 0,
    "windowHandle": null
  },
  "capture": {
    "format": "png",
    "bytes": 4123456,
    "width": 1920,
    "height": 1080
  },
  "attachment": {
    "format": "jpg",
    "mimeType": "image/jpeg",
    "bytes": 325658,
    "width": 1600,
    "height": 900
  },
  "delivery": {
    "event": "agent.request",
    "channel": "discord",
    "to": "channel:1475224687599554842",
    "sessionKey": "agent:main:..."
  }
}
```

### `mode=file`

```json
{
  "ok": true,
  "mode": "file",
  "target": {
    "source": "screen",
    "screenIndex": 0,
    "windowHandle": null
  },
  "capture": {
    "format": "png",
    "bytes": 4123456,
    "width": 1920,
    "height": 1080
  },
  "file": {
    "path": "C:\\path\\to\\shot.png",
    "format": "png",
    "mimeType": "image/png",
    "bytes": 4123456,
    "width": 1920,
    "height": 1080
  }
}
```

### `mode=data`

```json
{
  "ok": true,
  "mode": "data",
  "target": {
    "source": "screen",
    "screenIndex": 0,
    "windowHandle": null
  },
  "capture": {
    "format": "png",
    "bytes": 4123456,
    "width": 1920,
    "height": 1080
  },
  "inline": {
    "format": "jpg",
    "mimeType": "image/jpeg",
    "bytes": 325658,
    "width": 1600,
    "height": 900,
    "base64": "..."
  }
}
```

## Guardrail for inline payloads

For `mode=data`, if encoded attachment bytes exceed `maxInlineBytes`, return `InvalidRequest` with guidance to use `mode=deliver` or `mode=file`.

## Internal pipeline

1. Capture raw image bytes once (`screen` or `window` source)
2. Branch by mode:
   - `deliver`: JPEG encode (maxWidth/quality), send via `node.event -> agent.request`
   - `file`: write output bytes to `outputPath`
   - `data`: JPEG encode and return inline base64 (size-capped)

## Test expectations

- `deliver` routes with strict `channel` + `to`
- `deliver` response is metadata-only (no inline base64 field)
- `file` writes and returns file metadata
- `data` under cap succeeds
- `data` over cap fails with actionable error
- invalid mode rejected
- legacy params rejected
