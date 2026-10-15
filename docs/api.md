# API reference

Base path: `/api`. JSON uses camelCase. Timestamps are ISO 8601 UTC. Durations are milliseconds unless the name says otherwise. When `Api__EnableSwagger=true` (the default in Development), OpenAPI is served at `/swagger/v1/swagger.json` with a UI at `/swagger`.

## Errors (non-streaming)

Every non-2xx response is RFC 7807 Problem Details (`application/problem+json`) with two extension members:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.4",
  "title": "Inference provider unavailable",
  "status": 503,
  "detail": "The local inference service is not reachable. It may be stopped or still starting.",
  "code": "provider_unavailable",
  "retryable": true,
  "traceId": "00-7ea9f6cf…-00"
}
```

Validation failures add an `errors` map:

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "detail": "The chat request is invalid.",
  "errors": {
    "model": ["The model identifier is not valid."],
    "messages[0].content": ["Message content must not be empty."]
  },
  "code": "validation_failed",
  "retryable": false
}
```

| Status | `code` |
|---|---|
| 400 | `validation_failed`, `invalid_request` (malformed or missing JSON) |
| 403 | `model_not_allowed` |
| 404 | `model_not_found` |
| 413 | `payload_too_large` |
| 429 | `too_many_generations` |
| 500 | `internal_error` (generic message; details only in server logs) |
| 502 | `provider_error`, `provider_invalid_response`, `model_load_failed` |
| 503 | `provider_unavailable` |
| 504 | `provider_timeout` |

## `GET /api/health`

Liveness only. Always 200 while the process runs.

```json
{ "status": "healthy", "service": "LocalAI.Api", "version": "1.0.0", "environment": "Production",
  "startedAt": "2026-10-15T02:02:46.94Z", "uptimeSeconds": 26, "timestamp": "2026-10-15T02:03:13.18Z" }
```

## `GET /api/health/ready`

Returns 200 only when the provider is reachable **and** at least one usable model is installed; otherwise 503 with the same body shape.

```json
{
  "status": "degraded",
  "checks": {
    "api": { "status": "pass", "detail": null },
    "provider": { "status": "pass", "detail": "ollama 0.40.4 reachable" },
    "models": { "status": "fail", "detail": "No usable model is installed. Pull one with 'ollama pull <model>'." }
  },
  "checkedAt": "2026-10-15T02:03:13.24Z"
}
```

`status` is one of `ready`, `degraded`, or `unavailable`. Each check is `pass`, `fail`, or `unknown`.

## `GET /api/models`

Query: `refresh=true` bypasses the 5-second cache. Returns 503 Problem Details if Ollama is unreachable. An empty `models` array means nothing is installed; names are never invented.

```json
{
  "provider": "ollama",
  "models": [
    {
      "name": "qwen2.5:0.5b",
      "displayName": "qwen2.5",
      "tag": "0.5b",
      "family": "qwen2",
      "digest": "a8b0c5157701",
      "sizeBytes": 397821319,
      "modifiedAt": "2026-10-15T02:03:40Z",
      "parameterSize": "494.03M",
      "quantizationLevel": "Q4_K_M",
      "isRemote": false,
      "isAvailable": true,
      "isDefault": true
    }
  ],
  "defaultModel": "qwen2.5:0.5b",
  "fetchedAt": "2026-10-15T02:04:10Z"
}
```

- `isAvailable`: the model can be used for inference. It is false for remote models unless `Chat__AllowRemoteModels=true`.
- `isRemote`: Ollama proxies the model to another host (a "cloud" model). `sizeBytes` is `null` for these.
- `isDefault`: the model matches `Chat__DefaultModel`.

## `GET /api/models/status`

Always 200; inspect the body.

```json
{
  "provider": {
    "provider": "ollama", "reachable": true, "version": "0.40.4", "latencyMs": 3.1, "error": null,
    "checkedAt": "…", "lastSuccessfulCheckAt": "…",
    "installedModelCount": 1, "usableModelCount": 1,
    "defaultModel": "qwen2.5:0.5b", "defaultModelInstalled": true
  },
  "lastSuccessfulInferenceAt": "2026-10-15T02:04:10Z"
}
```

When Ollama is unreachable, `reachable` is `false`, `error` carries a safe message, and the counts are `null`.

## Chat request body (both chat endpoints)

```json
{
  "model": "qwen2.5:0.5b",
  "messages": [
    { "role": "system", "content": "You are concise." },
    { "role": "user", "content": "What is SSE?" }
  ],
  "temperature": 0.7,
  "maxTokens": 512
}
```

| Field | Rules (defaults) |
|---|---|
| `model` | Required. Pattern `name[:tag]` using `[A-Za-z0-9._-/]`, ≤ 200 chars. Must be installed and usable. `name` also matches `name:latest`. |
| `messages` | 1–`MaxMessages` (50). `role` ∈ `system`, `user`, `assistant`. `content` must be non-empty and non-whitespace, ≤ `MaxMessageLength` (8,000 chars). The total is ≤ `MaxConversationLength` (32,000 chars). **The last message must be `user`.** |
| `temperature` | Optional, 0–2. Omitted means the model default. |
| `maxTokens` | Optional, 1–`MaxTokensLimit` (4096). Default `DefaultMaxTokens` (1024). Maps to Ollama `num_predict`. |

Concurrency: at most `Api__MaxConcurrentGenerations` (2) chat requests run at once. Beyond that the API returns 429 immediately, with no queueing.

## `POST /api/chat/stream`

Response: `200 text/event-stream; charset=utf-8`, with `Cache-Control: no-cache, no-transform` and `X-Accel-Buffering: no`.

Validation, model resolution, and provider reachability are checked **before** the stream starts, so those failures are normal Problem Details responses (400/403/404/429/503). After the `200`, the body is a sequence of events:

```
start  →  delta*  →  ( done | error )
```

Each event is one `event:` line, one `data:` line of compact JSON, and a blank line. If the client disconnects, the stream simply ends and no terminal event is sent.

| Event | Payload | Notes |
|---|---|---|
| `start` | `{"requestId":"0HN…","model":"qwen2.5:0.5b","createdAt":"…"}` | Sent once, immediately. `model` is the resolved canonical name. |
| `delta` | `{"content":"Hel"}` | Incremental text, appended in order. Empty deltas are not sent. |
| `done` | `{"finishReason":"stop","stats":{…}}` | Terminal. `finishReason` comes from the provider (`stop`, `length`, …). |
| `error` | `{"code":"provider_timeout","message":"…","retryable":true}` | Terminal. Text already received stays valid. |

`stats` fields (each may be `null`): `promptTokens`, `completionTokens`, `totalDurationMs`, `loadDurationMs`, `generationDurationMs`, `tokensPerSecond`. All are reported by Ollama, not estimated.

Example (the `done` stats are from a real run on the Docker stack; IDs and timestamps are illustrative, deltas abridged):

```
event: start
data: {"requestId":"0HN7H3K2M9V1Q:00000001","model":"qwen2.5:0.5b","createdAt":"2026-10-15T02:04:10.11Z"}

event: delta
data: {"content":"1"}

event: delta
data: {"content":","}

event: done
data: {"finishReason":"stop","stats":{"promptTokens":42,"completionTokens":50,"totalDurationMs":2162.1,"loadDurationMs":1040.55,"generationDurationMs":930.81,"tokensPerSecond":53.72}}

```

To cancel, close the connection (abort the `fetch`, or Ctrl-C `curl`). The server cancels the upstream Ollama request and counts the generation as `cancelled`.

```bash
curl -N -X POST http://127.0.0.1:5080/api/chat/stream \
  -H 'Content-Type: application/json' \
  -d '{"model":"qwen2.5:0.5b","messages":[{"role":"user","content":"Hello"}]}'
```

## `POST /api/chat`

Same request body. It buffers the full reply and returns JSON. Provider failures map to Problem Details (502/503/504).

```json
{
  "model": "qwen2.5:0.5b",
  "message": { "role": "assistant", "content": "OK" },
  "finishReason": "stop",
  "stats": { "promptTokens": 33, "completionTokens": 2, "totalDurationMs": 61.2, "loadDurationMs": 12.0, "generationDurationMs": 7.6, "tokensPerSecond": 263.4 }
}
```

## `GET /api/system/overview`

A snapshot for the dashboard (example values below are illustrative). Every number comes from the running system; counters are in-memory since `inference.since` (the API start).

```json
{
  "api": { "status": "healthy", "version": "1.0.0", "environment": "Production", "startedAt": "…", "uptimeSeconds": 45 },
  "provider": { "provider": "ollama", "reachable": true, "version": "0.40.4", "latencyMs": 3, "error": null,
                "checkedAt": "…", "lastSuccessfulCheckAt": "…", "installedModelCount": 1, "usableModelCount": 1,
                "defaultModel": "qwen2.5:0.5b", "defaultModelInstalled": true },
  "inference": {
    "since": "…", "totalRequests": 3, "succeeded": 1, "failed": 1, "cancelled": 1, "activeGenerations": 0,
    "lastSuccessAt": "…", "lastFailureAt": "…",
    "lastCompletionModel": "qwen2.5:0.5b",
    "lastCompletion": { "promptTokens": 50, "completionTokens": 402, "totalDurationMs": 6300, "loadDurationMs": 1000, "generationDurationMs": 5100, "tokensPerSecond": 78.8 },
    "recentFailures": [ { "at": "…", "code": "model_not_found", "model": "ghost:1b" } ]
  },
  "limits": { "maxMessageLength": 8000, "maxConversationLength": 32000, "maxMessages": 50, "defaultMaxTokens": 1024,
              "maxTokensLimit": 4096, "maxConcurrentGenerations": 2, "inferenceTimeoutSeconds": 300, "allowRemoteModels": false },
  "generatedAt": "…"
}
```

`totalRequests` counts chat requests that passed validation. `failed` covers both pre-stream failures (such as `model_not_found`) and mid-stream failures. `recentFailures` holds the last 10.
