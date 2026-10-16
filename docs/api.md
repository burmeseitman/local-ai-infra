# API reference

Base path: `/api`. JSON uses camelCase. Timestamps are ISO 8601 UTC. Durations are milliseconds unless the name says otherwise. When `Api__EnableSwagger=true` (the default in Development), OpenAPI is served at `/swagger/v1/swagger.json` with a UI at `/swagger`.

## CSRF header

**Every** `POST`, `PATCH`, and `DELETE` under `/api` must include `X-LocalAI-Request: 1` (any value works). Without it the API answers `403` with `code: "missing_request_header"`. Browsers can't attach custom headers to cross-site form posts without a CORS preflight, so this closes the gap `SameSite` cookies leave open for other ports on `localhost`. The dashboard sends it automatically; curl users add `-H 'X-LocalAI-Request: 1'`.

## Authentication

The API uses **cookie sessions**. The cookie (`localai.session`) is HttpOnly and `SameSite=Strict`, lasts 7 days, and renews while in use.

- **Anonymous endpoints:** `GET /api/health`, `GET /api/health/ready`, `GET /api/auth/state`, `POST /api/auth/setup`, `POST /api/auth/login`, and `POST /api/auth/logout`.
- **Everything else** requires a session. Without one the API answers `401` with `code: "unauthenticated"`.
- **User administration** (`/api/users`) also requires the admin role, otherwise `403` with `code: "forbidden"`.
- **Session re-validation:** every request checks the session against the account's *session version*. Deleting a user, resetting their password, changing their role, or changing your own password ends the affected sessions immediately. A password change keeps the session that made it.

| Status | `code` | When |
|---|---|---|
| 400 | `validation_failed` | Username not 3–32 of `[A-Za-z0-9._-]`, password shorter than `Auth:MinPasswordLength` (10) or longer than 128 |
| 401 | `unauthenticated` | No or expired session |
| 401 | `invalid_credentials` | Wrong username or password. The message is the same for both, and so is the timing |
| 403 | `forbidden` | Not an administrator |
| 404 | `user_not_found` | Unknown user id |
| 409 | `setup_completed` | `POST /api/auth/setup` after an account exists |
| 409 | `username_taken` | Usernames are unique, ignoring case |
| 409 | `last_admin` | Would remove or demote the only administrator |
| 409 | `cannot_modify_self` | Deleting yourself or changing your own role |
| 429 | `too_many_attempts` | Too many failed sign-ins for this username (default 5 within 5 minutes). A `Retry-After` header gives the wait in seconds |

### `GET /api/auth/state`

```json
{ "setupRequired": false, "user": { "id": "…", "username": "admin", "role": "admin", "createdAt": "…", "lastLoginAt": "…" } }
```

`user` is `null` when not signed in. `setupRequired` is `true` only while no account exists.

### `POST /api/auth/setup` and `POST /api/auth/login`

Body: `{ "username": "admin", "password": "…" }`. On success, returns the user object and sets the session cookie. Setup creates an **administrator**, and only works once. Conversations created before accounts existed are assigned to that administrator.

### `POST /api/auth/logout` → `204`

### `POST /api/auth/password` → `204`

Body: `{ "currentPassword": "…", "newPassword": "…" }`. Validation errors use the key `newPassword`.

### `/api/users` (admin only)

| Method | Path | Body | Result |
|---|---|---|---|
| GET | `/api/users` | — | `{ "users": [UserDto…] }`, sorted by username. Hashes are never returned |
| POST | `/api/users` | `{ "username", "password", "role": "user" \| "admin" }` | `201` + `UserDto` |
| PATCH | `/api/users/{id}` | `{ "role"?: "user" \| "admin", "password"?: "…" }` | `204`. Signs that user out |
| DELETE | `/api/users/{id}` | — | `204`. **Deletes their conversations too** |

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
curl -N -b jar -X POST -H 'X-LocalAI-Request: 1' http://127.0.0.1:5080/api/chat/stream \
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

## Conversations (Phase 2)

Conversations belong to the signed-in user. Another user's conversation id behaves exactly like a missing one (`404 conversation_not_found`), even while it is busy. History is stored on the server in SQLite. Clients send **only the new message**. The API loads the stored history, chooses the context window, persists the user message, streams the reply, and persists the reply when the stream ends:

- `complete`: with stats.
- `stopped`: the client disconnected or pressed Stop. Partial text is kept.
- `error`: with `error.code` and `error.message`. Partial text is kept.

Additional error codes:

| Status | `code` | When |
|---|---|---|
| 404 | `conversation_not_found` | Unknown or deleted conversation id |
| 409 | `conversation_busy` | A reply is already streaming in this conversation (sending, regenerating, or deleting) |
| 409 | `nothing_to_regenerate` | Regenerate requested but the conversation has no user message to answer |

### `GET /api/conversations?limit=50`

`limit` is clamped to 1–200. Results are ordered by `updatedAt`, newest first.

```json
{ "conversations": [
  { "id": "30810114-a543-484c-852e-6c68ac9db208", "title": "My name is Minh. Reply with one short sentence.",
    "createdAt": "…", "updatedAt": "…", "messageCount": 4, "lastModel": "qwen2.5:0.5b" } ] }
```

### `POST /api/conversations`

Body: `{ "title": "optional" }`. Returns **201** with a `Location` header and the `Conversation` object (empty `messages`). If no title is given, the first user message (first 60 characters, whitespace collapsed) becomes the title.

### `GET /api/conversations/{id}`

```json
{
  "id": "30810114-…", "title": "…", "createdAt": "…", "updatedAt": "…",
  "messages": [
    { "id": "…", "sequence": 1, "role": "user", "content": "What is my name?", "status": "complete",
      "model": null, "error": null, "stats": null, "createdAt": "…" },
    { "id": "…", "sequence": 2, "role": "assistant", "content": "Your name is Minh.", "status": "complete",
      "model": "qwen2.5:0.5b", "error": null,
      "stats": { "promptTokens": 61, "completionTokens": 6, "totalDurationMs": 613, "loadDurationMs": 21, "generationDurationMs": 530, "tokensPerSecond": 11.3 },
      "createdAt": "…" },
    { "id": "…", "sequence": 4, "role": "assistant", "content": "Part", "status": "error", "model": "qwen2.5:0.5b",
      "error": { "code": "provider_timeout", "message": "The inference service did not respond within 300 seconds." },
      "stats": null, "createdAt": "…" }
  ]
}
```

### `PATCH /api/conversations/{id}`

Body: `{ "title": "New title" }` (1–120 characters after trimming). Returns **204**.

### `DELETE /api/conversations/{id}`

Returns **204**. Messages are deleted with the conversation. Returns 409 while a reply is streaming.

### `POST /api/conversations/{id}/messages/stream`

```json
{ "model": "qwen2.5:0.5b", "content": "What is my name?", "temperature": 0.2, "maxTokens": 256 }
```

- `content` follows the same rules as a chat message (non-empty, at most `MaxMessageLength`). Validation errors use the key `content`.
- `model`, `temperature`, and `maxTokens` are validated exactly as for `/api/chat`.
- The SSE response is identical to `/api/chat/stream`, except that `start` includes `"conversationId"`.
- **Context window:** the newest eligible stored messages that fit `Chat:MaxMessages` and `Chat:MaxConversationLength` (characters). Eligible means all user messages, plus assistant replies that are `complete` or `stopped` and non-empty. Failed replies are never sent to the model.
- If validation fails, the model is unknown, or Ollama is down, the API responds **before** storing anything, so no orphan user message is left behind.

**Document chat:** with `"useDocuments": true`, the API embeds the message and picks up to `Rag:TopK` (5) passages from **your** ready documents with cosine similarity of at least `Rag:MinScore` (0.5). It sends them to the model in a system message, numbered `[1]`, `[2]`, …, with instructions to cite them and to treat them as data, not instructions. The stream then includes a `sources` event right after `start`:

```
event: sources
data: {"sources":[{"number":1,"documentId":"…","title":"handbook","page":1,"snippet":"Section 2: Vacation policy. Every full-time employee receives 27 paid vacation days…","score":0.789}]}
```

An empty `sources` array means nothing matched. The model is then told to say so and to answer from general knowledge. Sources are stored with the assistant message (`sources` in `GET /api/conversations/{id}`) and the system message is not.

### `POST /api/conversations/{id}/regenerate/stream`

Body: `{ "model": "qwen2.5:0.5b", "temperature": null, "maxTokens": null }`.

Deletes the last assistant reply, if the last message is one, and streams a new reply to the last user message. This is what the UI's **Retry** button calls. The SSE format is the same as above.

```bash
# After signing in with: curl -c jar -X POST -H 'X-LocalAI-Request: 1' localhost:5080/api/auth/login ...
ID=$(curl -s -b jar -X POST -H 'X-LocalAI-Request: 1' localhost:5080/api/conversations -H 'Content-Type: application/json' -d '{}' | jq -r .id)
curl -N -b jar -X POST -H 'X-LocalAI-Request: 1' localhost:5080/api/conversations/$ID/messages/stream \
  -H 'Content-Type: application/json' -d '{"model":"qwen2.5:0.5b","content":"Hello"}'
curl -s -b jar localhost:5080/api/conversations/$ID | jq
```

The stateless `/api/chat` and `/api/chat/stream` endpoints remain available for integrations that manage their own history.

## Documents (Phase 3)

Documents are private to their owner (another user's id → 404). Status changes from `processing` to `ready` or `failed`.

### `GET /api/documents`

```json
{
  "documents": [
    { "id": "…", "title": "handbook", "fileName": "handbook.pdf", "contentType": "application/pdf", "sizeBytes": 20379,
      "characterCount": 9762, "chunkCount": 10, "status": "ready", "error": null, "createdAt": "…", "processedAt": "…" }
  ],
  "embeddingModel": "nomic-embed-text",
  "embeddingModelInstalled": true,
  "maxFileBytes": 10485760,
  "supportedExtensions": [".pdf", ".txt", ".md", ".markdown"]
}
```

### `POST /api/documents` → `202 Accepted`

Send `multipart/form-data` with the file in a field named `file`. The text is extracted and validated during the request, then chunking and embedding continue in the background. The response is the document (status `processing`) with a `Location` header; poll `GET /api/documents/{id}` until `status` is `ready` or `failed`.

```bash
curl -b jar -H 'X-LocalAI-Request: 1' -F file=@handbook.pdf localhost:5080/api/documents
```

| Status | `code` | When |
|---|---|---|
| 400 | `validation_failed` | Not multipart, or no `file` field |
| 409 | `embedding_model_missing` | `Rag:EmbeddingModel` isn't installed (the message includes the `ollama pull` command) |
| 409 | `document_limit_reached` | More than `Rag:MaxDocumentsPerUser` |
| 413 | `document_too_large` / `payload_too_large` | Over `Rag:MaxFileBytes` (10 MB, enforced while reading), or extracted text over `Rag:MaxDocumentCharacters` |
| 415 | `unsupported_document_type` | Not `.pdf`, `.txt`, `.md`, or `.markdown` |
| 422 | `document_has_no_text` | Nothing extractable (for example a scanned PDF) |
| 422 | `document_unreadable` | A damaged or password-protected PDF, or a binary file named `.txt` |

When background indexing fails, the document is marked `failed` with a safe `error` message (for example "The embedding model 'nomic-embed-text' is not installed…").

### `GET /api/documents/{id}` and `DELETE /api/documents/{id}` → `204`

Deleting removes the document and its passages. Sources already stored in conversations keep their title and snippet.

Embedding models appear in `GET /api/models` with `"isEmbedding": true` and `"isAvailable": false`. They can't be used for chat (`model_not_allowed`).
