# Development guide

Each piece runs independently. Typical inner loop: Ollama native (or in Docker), the API with `dotnet run`, and the dashboard with `npm run dev`.

## 1. Ollama (provider)

**Option A: native install** (fastest; uses Metal on Apple Silicon or CUDA on NVIDIA):

```bash
ollama serve                       # if not already running as a service
ollama pull qwen2.5:0.5b
curl http://localhost:11434/api/version
```

For document chat, also run `ollama pull nomic-embed-text` (~270 MB).

**Option B: the compose container, published on loopback port 11435** (avoids clashing with a native Ollama on 11434):

```bash
docker compose -f docker-compose.yml -f infrastructure/compose/docker-compose.ollama-port.yml up -d ollama
docker compose exec ollama ollama pull qwen2.5:0.5b
curl http://127.0.0.1:11435/api/version
# then run the API with: Ollama__BaseUrl=http://127.0.0.1:11435
```

Note: if your native Ollama has "cloud" models (e.g. `glm-5:cloud`), the API lists them as remote and refuses them unless `Chat__AllowRemoteModels=true`.

## 2. Backend (API)

```bash
dotnet restore
dotnet build LocalAI.sln                 # warnings are errors
dotnet run --project src/LocalAI.Api --launch-profile http
```

- Listens on `http://localhost:5080`. Swagger UI is at `http://localhost:5080/swagger` (Development only).
- Default `Ollama__BaseUrl` is `http://localhost:11434`. Override it with an environment variable, user secrets, or `appsettings.Development.json`.
- Configuration errors fail fast at startup, naming the invalid setting.

**Only a newer .NET SDK installed (e.g. .NET 10)?** The projects target `net8.0`, and a newer SDK compiles them fine. To run them without the .NET 8 runtime, allow roll-forward:

```bash
DOTNET_ROLL_FORWARD=Major dotnet run --project src/LocalAI.Api --launch-profile http
```

That's fine for local development. The authoritative build and test environment is the `mcr.microsoft.com/dotnet/sdk:8.0` image (see Tests).

Conversation history is stored in `src/LocalAI.Api/data/localai.db` (git-ignored). It's created and migrated automatically at startup. Delete the file to start fresh, or set `Persistence__ConnectionString`.

**First run:** open the dashboard, or call `POST /api/auth/setup`, to create the administrator. Session-signing keys are stored in `src/LocalAI.Api/data/keys` (git-ignored). To start completely fresh locally, stop the API and delete `src/LocalAI.Api/data/`.

Smoke test (`-c`/`-b` keep the session cookie):

```bash
curl -s localhost:5080/api/health/ready | jq
read -rs -p "Admin password: " PW; echo
curl -s -c jar -X POST -H 'X-LocalAI-Request: 1' localhost:5080/api/auth/login -H 'Content-Type: application/json' \
  -d "{\"username\":\"admin\",\"password\":\"$PW\"}"
curl -N -b jar -X POST -H 'X-LocalAI-Request: 1' localhost:5080/api/chat/stream -H 'Content-Type: application/json' \
  -d '{"model":"qwen2.5:0.5b","messages":[{"role":"user","content":"Say hi"}]}'
```

### Database migrations

The schema is managed with EF Core migrations in `src/LocalAI.Persistence/Migrations`, using a pinned local tool (`dotnet-tools.json`, dotnet-ef 8.0.11):

```bash
dotnet tool restore
# after changing an entity or LocalAiDbContext:
dotnet ef migrations add <Name> -p src/LocalAI.Persistence -s src/LocalAI.Persistence -o Migrations
```

With only a newer .NET runtime installed, prefix the command with `DOTNET_ROLL_FORWARD=Major`. Migrations are applied automatically when the API starts. `SqliteConversationStoreTests.Migrations_are_up_to_date_with_the_model` fails if you change the model without adding a migration.

## 3. Frontend (dashboard)

```bash
cd src/LocalAI.Web
npm ci
npm run dev            # http://localhost:5173, proxies /api → http://localhost:5080
```

- Point the proxy elsewhere with `VITE_DEV_API_PROXY=http://host:port npm run dev`.
- `VITE_API_BASE_URL` is only for a cross-origin API. The API must then list the dashboard origin in `Api__CorsAllowedOrigins`.
- Other scripts: `npm run build`, `npm run preview`, `npm run lint`, `npm run typecheck`, `npm test`, `npm run test:watch`.

Code layout (`src/`):

| Folder | Contents |
|---|---|
| `api/` | `fetch` wrappers, `ApiError`, and the SSE chat client (`chat-stream.ts`) |
| `features/chat/` | Reducers (`chat-state.ts`), `useConversation` (server-backed chat), message list and item, composer |
| `features/conversations/` | History panel: list, select, inline rename, two-step delete |
| `features/auth/` | `useAuth` (session state; any `unauthenticated` response signs the UI out) and the setup/sign-in screen |
| `pages/` | Also `users-page` (admin), `account-page` (change password), and `documents-page` (upload and indexing status) |
| `features/models/` | Selection logic, selector, install help |
| `features/system/` | Health-layer derivation and the sidebar status |
| `hooks/` | `useAsyncResource` (load + poll + keep last good data) and the hash router |
| `pages/` | Chat, Models, System |
| `components/` | Markdown (sanitized), status dot, notices, icons, copy button |

## 4. Everything in Docker

On a Mac, prefer **native Ollama + containers for web and api**. It's about 4× faster, because the GPU is used:

```bash
ollama pull qwen2.5:7b && ollama pull nomic-embed-text
docker compose -f docker-compose.yml -f infrastructure/compose/docker-compose.native-ollama.yml up -d web
```

Fully containerized (CPU only on a Mac):

```bash
docker compose up -d --build
docker compose logs -f api
docker compose down          # keeps models; add -v to delete the ollama-models volume
```

## Tests

| What | Command |
|---|---|
| All backend tests on .NET 8 | `docker build -f infrastructure/docker/api.Dockerfile --target test .` |
| Backend tests locally | `dotnet test LocalAI.sln` |
| One backend project | `dotnet test tests/LocalAI.Providers.Tests` |
| One backend test | `dotnet test --filter "FullyQualifiedName~Cancellation"` |
| Frontend tests | `cd src/LocalAI.Web && npm test` |
| One frontend test | `npx vitest run -t "stops a generation"` |
| Frontend in Docker | `docker build -f infrastructure/docker/web.Dockerfile --target test .` |

`tests/LocalAI.Persistence.Tests` runs the store against an in-memory SQLite database with the real migrations. `tests/LocalAI.Api.Tests` hosts the real pipeline (with a temporary SQLite file per test class) through `WebApplicationFactory<Program>`, with `IChatProvider` replaced by `tests/Shared/FakeChatProvider.cs`. `ApiFactory.CreateAuthenticatedClientAsync()` runs first-run setup and returns a client that holds the session cookie, and anonymous clients test the 401 paths. `tests/LocalAI.Providers.Tests` drives `OllamaClient` through a stub `HttpMessageHandler` and a `SegmentedStream` that splits responses at arbitrary byte boundaries. No test needs Ollama or a model.

> With only the .NET 10 runtime, `LocalAI.Api.Tests` fails with `ResponseBodyPipeWriter does not implement PipeWriter.UnflushedBytes`. That's an incompatibility between the .NET 8 `TestHost` package and the .NET 10 System.Text.Json, not an application bug. Install the .NET 8 runtime or use the Docker test stage. Core, Providers and Persistence tests run fine with roll-forward.

## Conventions

- C#: file-scoped namespaces, nullable enabled, analyzers at `8.0-recommended`, warnings are errors.
- TypeScript: strict mode plus `noUncheckedIndexedAccess`, kebab-case file names, double quotes, semicolons, and ESLint with the React Hooks (compiler) rules.
- Every state-changing `fetch` must include `CSRF_HEADERS` from `api/http.ts`; the API rejects it otherwise.
- Never log prompt, response, or document content. Log the model, sizes, durations, and error codes.
- New provider: implement `IChatProvider` in `LocalAI.Providers/<Name>/` and add an `Add<Name>Provider` extension. See the contract in [architecture.md](architecture.md#provider-abstraction).
