# Plan: alcatraz.cli — customer-facing CLI

## Context

`alcatraz.cli` is the customer's entry point into the Alcatraz product: log in via OAuth device flow, manage sandboxes, fetch a short-lived SSH cert, and SSH into a Firecracker microVM. The end-to-end design is fixed in `plans/customer-vm-access-ssh-ca.md` (system of record for SSH CA + device flow + gateway), and the API contract it consumes is `plans/alcatraz-api-cli-endpoints.md`. This plan covers the customer-facing CLI itself, plus three supporting changes:

1. A new `POST /api/v1/auth/refresh` endpoint on `alcatraz.api`, so the CLI can refresh tokens without holding Keycloak's `client_secret` (preserves the "API proxies Keycloak; CLI never knows realm/client_id/secret" invariant).
2. Consolidation of the two existing per-component compose files (`alcatraz.api/docker-compose.yml`, `alcatraz.worker/docker-compose.yml`) into a single repo-root `docker-compose.yml`, with the worker dropped (host-run) and the CLI built locally.
3. The CLI itself, at `/alcatraz.cli/`, with full command tree, device-flow login, token refresh, sandbox CRUD, and stock-`ssh` invocation.

Locked-in decisions:

- **Binary name:** `alcatraz` (project `Alcatraz.Cli`, AssemblyName `alcatraz`).
- **Framework:** Spectre.Console.Cli with type-safe settings.
- **`alcatraz ssh <id>`:** implemented now against the API-returned `gatewayHost`/`gatewayPort`. `--no-proxy` skips the `openssl s_client` TLS wrap for plain-TCP local sshd. Switches automatically to TLS-wrapped form when the API returns gatewayPort 443 or `alwaysUseGatewayProxy` is set in config.
- **Token refresh:** new `POST /api/v1/auth/refresh` proxy endpoint on the API; CLI consumes it via a `BearerHandler` `DelegatingHandler`.
- **Output:** Spectre tables by default; `--json` for machine-readable output.
- **Local dev orchestration:** single root-level `docker-compose.yml`. Worker runs manually on host (binds to `localhost:4222` for NATS and `localhost:5341` for Seq, matching `alcatraz.worker/.env`). CLI is built locally with `dotnet`, never in compose.

## Endpoints consumed

The CLI talks to these API endpoints; the device flow is proxied through the API so the CLI never holds Keycloak's `client_secret`.

| Method | Path | Auth | CLI command |
|---|---|---|---|
| POST | `/api/v1/auth/device` | anon | `alcatraz login` (initiate) |
| POST | `/api/v1/auth/device/token` | anon | `alcatraz login` (poll) |
| POST | `/api/v1/auth/refresh` | anon | `BearerHandler` (silent refresh) — **new this round** |
| POST | `/api/v1/sandboxes` | bearer | `alcatraz sandbox create` |
| GET | `/api/v1/sandboxes` | bearer | `alcatraz sandbox list` |
| GET | `/api/v1/sandboxes/{id}` | bearer | `alcatraz sandbox get`, `alcatraz ssh` (pre-flight) |
| DELETE | `/api/v1/sandboxes/{id}` | bearer | `alcatraz sandbox delete` |
| POST | `/api/v1/sandboxes/{id}/ssh-cert` | bearer | `alcatraz sandbox ssh-cert`, `alcatraz ssh` |

## Project layout

CLI-local solution at `/alcatraz.cli/Alcatraz.Cli.sln`. Each repo component already owns its own build/test commands; we resist a repo-root mega-sln.

```
alcatraz.cli/
├── Alcatraz.Cli.sln
├── global.json                           # pin .NET 8 SDK band
├── README.md
├── docs/
│   └── local-end-to-end.md
├── src/Alcatraz.Cli/
│   ├── Alcatraz.Cli.csproj               # OutputType=Exe, AssemblyName=alcatraz, RootNamespace=Alcatraz.Cli
│   ├── Program.cs                        # 1-liner → CliBootstrap.RunAsync(args)
│   ├── Infrastructure/
│   │   ├── CliBootstrap.cs               # IConfiguration layering + CommandApp wiring
│   │   ├── DependencyInjection.cs        # AddAlcatrazCli(IServiceCollection, IConfiguration)
│   │   ├── TypeRegistrar.cs / TypeResolver.cs   # Spectre ↔ MS.DI bridge
│   │   └── CancellationContext.cs        # Ctrl+C → CancellationToken
│   ├── Configuration/                    # ConfigPathResolver, CliOptions, CliConfig, CliConfigStore
│   ├── Auth/                             # TokenStore, StoredTokens, DeviceFlowOrchestrator,
│   │                                     # BrowserLauncher, BearerHandler, JwtPayloadDecoder
│   ├── Api/                              # IAlcatrazApiClient, AlcatrazApiClient, DTOs, ApiErrors
│   ├── Ssh/                              # SshKeyManager, CertificateCache, SshLauncher
│   ├── Output/SandboxRenderer.cs
│   └── Commands/
│       ├── GlobalSettings.cs             # --api-url, --json, -v
│       ├── ExitCodes.cs / CommandRunner.cs
│       ├── Auth/LoginCommand.cs          # login + logout + whoami in one file
│       ├── Sandbox/{SandboxSettings,SandboxCommands}.cs
│       └── Ssh/SshCommand.cs
└── test/Alcatraz.Cli.UnitTests/          # mirrors src/, plus TempConfigDir helper
```

## Csproj package versions

CLI: `Spectre.Console 0.49.1`, `Spectre.Console.Cli 0.49.1`, `Spectre.Console.Json 0.49.1`, `Microsoft.Extensions.{Hosting,Http,Configuration,Configuration.Json,Configuration.EnvironmentVariables,Logging.Console,Options.ConfigurationExtensions} 8.0.0`. `System.Text.Json` is the BCL-bundled version.

Tests: `Microsoft.NET.Test.Sdk 17.10.0`, `xunit 2.9.0`, `xunit.runner.visualstudio 2.8.2`, `FluentAssertions 6.12.0`, `NSubstitute 5.1.0`, `RichardSzalay.MockHttp 7.0.0`, `coverlet.collector 6.0.2`. Mirrors `alcatraz.api`'s test stack.

Common csproj props: `<TargetFramework>net8.0</TargetFramework>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. `<InternalsVisibleTo Include="Alcatraz.Cli.UnitTests" />` so tests can reach internal types like `BearerHandler` and `AlcatrazApiClient`.

## Command tree

```
alcatraz login [--no-browser]
alcatraz logout
alcatraz whoami
alcatraz sandbox create [--vcpus N] [--memory MIB]
alcatraz sandbox list
alcatraz sandbox get <id>
alcatraz sandbox delete <id>
alcatraz sandbox ssh-cert <id> [--public-key PATH] [--out PATH]
alcatraz ssh <id> [remote-command] [--no-proxy]
```

Global flags via `GlobalSettings : CommandSettings`: `--api-url`, `--json`, `-v`. Settings types use `[CommandOption]`/`[CommandArgument]` and override `Validate()` for inline validation (e.g. `vcpus 1..16`, `memoryMib 512..32768` and multiple of 256, mirroring API-side FluentValidation rules).

Behaviors:

- **login** — POSTs `/auth/device`, renders Spectre `Panel` with `userCode` + `verificationUriComplete`, optionally opens browser, polls `/auth/device/token` at `interval` seconds (handles `slow_down` / `authorization_pending` / `expired_token` / `access_denied` per RFC 8628), saves tokens via `TokenStore`.
- **logout** — `TokenStore.Clear()`. No server call (RFC 8628 has no logout; access tokens just expire).
- **whoami** — local JWT decode of `idToken` for `sub` / `email` / `preferred_username` + access-token expiry. No signature verification.
- **sandbox create/list/get/delete** — direct API passthrough; render `SandboxResponse` as a table or JSON with `--json`. Status int → enum name.
- **sandbox ssh-cert** — pure cert fetch. Loads or generates `~/.config/alcatraz/id_alcatraz{.pub}` (ed25519), POSTs `/sandboxes/{id}/ssh-cert`, writes cert to cache, prints path + validity.
- **ssh** — pre-flight `GET /sandboxes/{id}` for friendly 404; ensures keypair; re-issues the cert (so we always have current gateway info); spawns `ssh` and propagates exit code.

## Configuration & state files

`ConfigPathResolver` (static helper):

- Linux/macOS: `XDG_CONFIG_HOME/alcatraz` if `XDG_CONFIG_HOME` is set, else `~/.config/alcatraz`.
- Windows: `%AppData%\alcatraz` via `Environment.SpecialFolder.ApplicationData`.

Files inside that dir:

- `config.json` — `{ apiBaseUrl, alwaysUseGatewayProxy }`. Default `apiBaseUrl=http://localhost:8080` (matches the root compose's API port).
- `tokens.json` — `{ accessToken, refreshToken, accessTokenExpiresAtUtc, tokenType, idToken }`. Separated from config so accidental config-sync tooling won't ship credentials.
- `id_alcatraz` + `id_alcatraz.pub` — workstation ed25519 keypair, generated by shelling `ssh-keygen -t ed25519 -f <path> -N "" -q -C alcatraz-cli` on first cert request.
- `certs/<sandbox-id>-cert.pub` + `<sandbox-id>-cert.json` (sidecar with `validUntilUtc` so we don't shell `ssh-keygen -L` to check expiry).
- `known_hosts` — passed to `ssh` via `-o UserKnownHostsFile=`, profile-local rather than `~/.ssh/known_hosts`.

POSIX file permissions: `0600` on `tokens.json` and `id_alcatraz`; `0700` on the directory. `File.SetUnixFileMode` (BCL, .NET 8). Skipped on Windows.

Configuration layering (`CliBootstrap.BuildConfiguration`): defaults → `~/.config/alcatraz/config.json` (via `CliConfigStore`) → environment variables (`ALCATRAZ_*`) → `--api-url` cmd-line override (parsed before Spectre so `IHttpClientFactory.BaseAddress` is right at construction).

## HTTP client architecture

Typed `IAlcatrazApiClient` registered via `AddHttpClient<IAlcatrazApiClient, AlcatrazApiClient>` with `BaseAddress` from `CliOptions.ApiBaseUrl`. Bearer attach + refresh + retry lives in `BearerHandler : DelegatingHandler` added via `.AddHttpMessageHandler<BearerHandler>()`.

`BearerHandler` rules:

- Skip bearer attach for requests that set `HttpRequestOptionsKey<bool>("anon")` (used by `InitiateDevice`, `PollDeviceToken`, `RefreshToken`).
- Refresh proactively when `expiresAtUtc - now < 60s` (single-process `SemaphoreSlim` to coalesce concurrent calls).
- On `401`, force a refresh-network-call (don't trust the cached "still fresh" expiry — token may have been revoked) and retry once. If second `401` or refresh failed, throw `NotLoggedInException`.
- Resolves `IAlcatrazApiClient` lazily via `IServiceProvider` (constructor-injected provider, not the client) to break the DI cycle.

Error mapping in `AlcatrazApiClient` (reads ProblemDetails on non-2xx):

- `400` extension `error ∈ {authorization_pending, slow_down}` → returned as `DeviceTokenExchangeResult { ErrorKind = … }` (no exception; orchestrator branches on it).
- `400` extension `error == expired_token` → `ExpiredDeviceCodeException`.
- `400` extension `error == access_denied` → `AuthorizationDeniedException`.
- `400` extension `error ∈ {invalid_grant, refresh_failed}` (refresh path) → `NotLoggedInException`.
- `400` other → `BadRequestException(detail)`.
- `404` on sandbox routes → `SandboxNotFoundException`.
- `409` → `ConflictException`.
- `5xx` / `HttpRequestException` → `ApiUnavailableException`.

`CommandRunner` is a small DI-resolved helper that wraps every async command body in a try/catch that maps these exceptions to user-friendly Spectre-rendered messages and exit codes.

## SSH wiring (`alcatraz ssh <id>`)

The SSH-CA design doc's argv shorthand `<vm-uuid>@gateway` resolves to: SSH user is **`al`** (the demo container's user, which sshd matches against `/etc/ssh/auth_principals/al` containing the sandbox UUID — that's where principal scoping happens, not in the username). Use `-o CertificateFile=` rather than a second `-i` for unambiguous cert handling across OpenSSH versions.

Local-demo argv (`gatewayPort != 443` and `alwaysUseGatewayProxy=false`):

```
ssh
  -i  ~/.config/alcatraz/id_alcatraz
  -o  CertificateFile=~/.config/alcatraz/certs/<id>-cert.pub
  -o  IdentitiesOnly=yes
  -o  UserKnownHostsFile=~/.config/alcatraz/known_hosts
  -o  StrictHostKeyChecking=accept-new
  -p  <gatewayPort>
  al@<gatewayHost>
  [<remote-command>]
```

Production argv (`gatewayPort == 443` or `alwaysUseGatewayProxy=true`): same, plus `-o ProxyCommand="openssl s_client -quiet -connect <gatewayHost>:<gatewayPort>"`. `--no-proxy` flag forces local-demo form regardless.

Process spawn: `ProcessStartInfo("ssh") { UseShellExecute = false }` + `psi.ArgumentList.Add(...)` for each arg (no shell escaping issues). stdin/stdout/stderr are NOT redirected so the user's terminal passes through unmolested for interactive shells. Returns `p.ExitCode` from the command.

CLI exit codes: `0` ok, `1` generic, `2` not logged in, `3` invalid args, `4` not found, `5` conflict, `>=64` propagated from `ssh`.

## API-side change: POST /api/v1/auth/refresh

Mirrors the existing device-token exchange exactly.

**Application layer** — `src/ForqStudio.Application/Auth/RefreshDeviceToken/`:

- `RefreshDeviceTokenCommand(string RefreshToken) : ICommand<DeviceTokenResponse>`.
- `RefreshDeviceTokenCommandValidator` — `RefreshToken` non-empty, max length 8192.
- `RefreshDeviceTokenCommandHandler` — forwards to `IDeviceAuthorizationClient.RefreshAsync`.

**Abstraction extension** — `IDeviceAuthorizationClient` gains `Task<Result<DeviceTokenResponse>> RefreshAsync(string refreshToken, CancellationToken ct)`. `DeviceAuthErrors` gains `RefreshFailed` and `InvalidGrant` (mapped from Keycloak's `error: invalid_grant`).

**Infrastructure** — `KeycloakDeviceAuthorizationClient.RefreshAsync`: form-POST to existing `KeycloakOptions.TokenUrl` with `grant_type=refresh_token`, `refresh_token`, `client_id=AuthClientId`, `client_secret=AuthClientSecret`. Body shape and success/error parsing mirror `ExchangeAsync` verbatim. No DI changes needed (typed `HttpClient` already registered).

**API layer** — `AuthController.RefreshDeviceToken([FromBody] RefreshDeviceTokenRequest)`. New DTO `RefreshDeviceTokenRequest(string RefreshToken)` next to `ExchangeDeviceTokenRequest`. `ResultExtensions.IsDeviceAuthError` extended to include the two new errors so they map to `400 + ProblemDetails extension error: invalid_grant` / `refresh_failed`.

**Tests:**

- `test/ForqStudio.Application.UnitTests/Auth/RefreshDeviceTokenCommandHandlerTests.cs` — happy path; `InvalidGrant` and `RefreshFailed` propagate.
- `test/ForqStudio.Application.UnitTests/Auth/RefreshDeviceTokenCommandValidatorTests.cs` — empty / overlong rejected.
- `test/ForqStudio.Api.FunctionalTests/Auth/DeviceFlowEndpointsTests.cs` — three new cases: happy refresh → 200; `invalid_grant` → 400 with `error: invalid_grant`; missing body → 400. Reuses the `IDeviceAuthorizationClient` substitute pattern from `Factory`.

## Docker Compose consolidation

The two pre-existing per-component compose files had overlapping NATS + Seq services and conflicting host ports — running them simultaneously was broken. They've been replaced by a single repo-root `docker-compose.yml`.

**Removed:**

- `alcatraz.api/docker-compose.yml`
- `alcatraz.api/docker-compose.override.yml`
- `alcatraz.worker/docker-compose.yml`

**Created:** `/docker-compose.yml` at the repo root — single source of truth for local dev infra.

### Services in the consolidated file

All retain their existing container names so internal DNS / appsettings continue to work:

| Service | Container name | Image / build | Host ports | Notes |
|---|---|---|---|---|
| `forqstudio.api` | `ForqStudio.Api` | build `./alcatraz.api/src` (Dockerfile `ForqStudio.Api/Dockerfile`) | `8080:8080` | Env: `ASPNETCORE_ENVIRONMENT=Development`, `Ssh__CA__PrivateKeyPath=/run/alcatraz-ca/alcatraz_ca`, `Nats__Url=nats://forqstudio-nats:4222`, `Gateway__Host=localhost`, `Gateway__Port=2222` (so `alcatraz ssh` auto-points at the demo sshd). Volume `alcatraz_ca:/run/alcatraz-ca:ro`. |
| `forqstudio-db` | `ForqStudio.Db` | `postgres:16` | `5432:5432` | Bind `./.containers/database:/var/lib/postgresql` (gitignored at repo root). Healthcheck `pg_isready`. |
| `forqstudio-idp` | `ForqStudio.Identity` | `quay.io/keycloak/keycloak:25.0` | `8082:8080` | Volume `keycloak_data:/opt/keycloak/data` + bind `./alcatraz.api/.files/forqstudio-realm-export.json:/opt/keycloak/data/import/realm.json`. `KC_HOSTNAME_URL=http://localhost:8082` preserves the device-flow URL fix. |
| `forqstudio-redis` | `ForqStudio.Redis` | `redis:7` | `6379:6379` | |
| `forqstudio-nats` | `ForqStudio.Nats` | `nats:2.10` | `4222:4222`, `8222:8222` | Worker connects to `localhost:4222`. |
| `forqstudio-seq` | `ForqStudio.Seq` | `datalust/seq:2024.3` | `5341:5341`, `8083:80` | Single Seq instance; worker also connects to `localhost:5341`. |
| `forqstudio-ca-init` | `ForqStudio.CaInit` | build `./alcatraz.api/.files/ca-init` | none | One-shot. Volume `alcatraz_ca:/run/alcatraz-ca`. Idempotent — skips if key already present. |
| `forqstudio-demo-sshd` | `ForqStudio.DemoSshd` | build `./alcatraz.api/.files/demo-sshd` | `2222:22` | Stand-in for a Firecracker VM; depends on ca-init. |

**Excluded:**

- `alcatraz.worker` — host-run via `make build && sudo -E ./bin/alcatraz-worker` (needs KVM + CNI + root). Existing `.env` already targets `localhost:4222` / `localhost:5341`, so no change required there.
- `alcatraz.cli` — built locally with `dotnet build alcatraz.cli/Alcatraz.Cli.sln`.

**Volumes:** `keycloak_data`, `alcatraz_ca` (named, declared at file root).

**Networks:** default bridge (no custom network needed; matches prior setup).

**Image pins:** specific majors instead of `:latest` so stack startup is deterministic across machines.

### Documentation updates

- Root `README.md` — "Local development" section rewritten for the consolidated compose; project layout diagram now lists `docker-compose.yml` and `alcatraz.cli`.
- `alcatraz.api/README.md` — "Running locally" section points at the root compose; CLI/worker per-component notes added.
- `alcatraz.worker/README.md` — "Run" section replaced with host-run flow + a note that the API stack must be running first.
- `alcatraz.api/docs/local-end-to-end.md` — walkthrough rewritten for repo-root compose; network name in the `nats sub` example updated to `alcatraz_default`.
- `alcatraz.api/docs/keycloak-auth-pipeline.md` — port reference updated from 5000 → 8080, Seq from 5341 → 8083.
- `alcatraz.api/.claude/CLAUDE.md` — "Running the Application" updated.
- `plans/alcatraz-api-cli-endpoints.md` (line 216) — verification snippet updated to the root-level compose.

## Implementation order

1. **Compose consolidation** — single root `docker-compose.yml`, delete old files, update all docs.
2. **API refresh endpoint** — `RefreshDeviceToken` command + handler + validator + `IDeviceAuthorizationClient.RefreshAsync` + `KeycloakDeviceAuthorizationClient.RefreshAsync` + controller route + `ResultExtensions` predicate + tests. `dotnet test alcatraz.api/ForqStudio.sln` must be green before touching the CLI.
3. **CLI scaffold** — solution, `global.json`, csproj with package versions pinned, project + test project skeletons, `dotnet build` succeeding on minimal `Program.cs`.
4. **CLI foundation** — `Configuration` → `TokenStore` → `IAlcatrazApiClient` + DTOs → `BearerHandler` → `DeviceFlowOrchestrator` → `LoginCommand`. End-to-end login working against the live API before adding sandbox commands.
5. **Sandbox commands** — `create/list/get/delete/ssh-cert` + their settings types + `SandboxRenderer`.
6. **SSH layer** — `SshKeyManager` → `CertificateCache` → `SshLauncher` → `SshCommand`. Verify against the demo sshd container.
7. **Run the verification script** end-to-end. Fix anything that fails.
8. **Persist this plan** as `plans/alcatraz-cli.md`. Update root `README.md`'s "Design references" + flip the `(planned)` marker on `alcatraz.cli`'s component description.

## Tests

Coverage requirement: every new code path has at least one unit test, every controller route gets a happy-path and a failure-path functional test, all suites green.

### CLI unit tests — `test/Alcatraz.Cli.UnitTests/`

- **Configuration:** `ConfigPathResolverTests` (Linux XDG set/unset). `CliConfigStoreTests` (round-trip, missing-file defaults).
- **Auth:** `TokenStoreTests` (mode 0600 on POSIX, missing returns null, `Clear` deletes). `BearerHandlerTests` (attach when fresh; proactive refresh near expiry; 401 → refresh + retry once with `forceNetworkCall`; bypass for `"anon"` requests; concurrent calls coalesce). `DeviceFlowOrchestratorTests` (happy path; access_denied; expired_token).
- **Api:** `AlcatrazApiClientTests` — happy paths + ProblemDetails decoding for `authorization_pending`, `invalid_grant`, 404 sandbox.
- **Ssh:** `SshLauncherTests` (argv builder for local-demo, production, remote-command tail). `CertificateCacheTests` (miss, hit, within-safety-margin → false, save round-trip).

### API-side tests (additions for the new refresh endpoint)

`RefreshDeviceTokenCommandHandlerTests` + `RefreshDeviceTokenCommandValidatorTests` + three new functional-test cases extending `DeviceFlowEndpointsTests`.

### Pre-existing API tests

The four existing API test projects all use Testcontainers — they don't depend on docker-compose, so the compose consolidation has no impact on them. They must remain green.

### Run-it-all

```bash
dotnet test alcatraz.cli/Alcatraz.Cli.sln           # CLI suite
dotnet test alcatraz.api/ForqStudio.sln             # API suite (incl. refresh endpoint tests)
cd alcatraz.worker && make test                     # worker suite (smoke)
dotnet build alcatraz.cli/Alcatraz.Cli.sln -c Release   # static checks under TreatWarningsAsErrors
```

All four must pass.

## End-to-end verification

```bash
cd /path/to/alcatraz   # repo root

# 1. Bring up the full stack from the root.
docker compose up -d
docker compose ps   # expect 7 services + ForqStudio.CaInit exited 0

# 2. EF migrations auto-apply on API startup (no manual `dotnet ef database update` needed).

# 3. Start the worker on the host (out of compose).
cd alcatraz.worker && make build && sudo -E ./bin/alcatraz-worker &
WORKER_PID=$!
cd ..

# 4. Build the CLI locally
dotnet build alcatraz.cli/Alcatraz.Cli.sln

# 5. CLI config (only needed if defaults don't match)
mkdir -p ~/.config/alcatraz && chmod 0700 ~/.config/alcatraz
cat > ~/.config/alcatraz/config.json <<'JSON'
{ "apiBaseUrl": "http://localhost:8080", "alwaysUseGatewayProxy": false }
JSON
chmod 0600 ~/.config/alcatraz/config.json

# 6. Register a Keycloak user (one-time, since the realm starts empty).
curl -sX POST http://localhost:8080/api/v1/users/register \
  -H 'content-type: application/json' \
  -d '{"email":"demo@alcatraz.local","firstName":"Demo","lastName":"User","password":"demopass"}'

# 7. Login via device flow (browser opens to Keycloak's verification page).
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- login

# 8. Sandbox CRUD
ID=$(dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- sandbox create \
       --vcpus 2 --memory 2048 --json | jq -r .id)
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- sandbox list

# 9. Wire the demo sshd's auth_principals to this sandbox UUID
#    (in production the worker writes this into the AgentFS overlay).
docker exec ForqStudio.DemoSshd sh -c "echo $ID > /etc/ssh/auth_principals/al"

# 10. SSH (interactive) and remote-command form
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- ssh "$ID"
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- ssh "$ID" "whoami; uname -s; hostname"

# 11. Inspect the cert
ssh-keygen -L -f ~/.config/alcatraz/certs/$ID-cert.pub
# expect: Type=user cert, Principal=$ID, Valid: now → +24h

# 12. JSON output and delete
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- sandbox list --json | jq .
dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- sandbox delete "$ID"

# 13. Refresh-token path (manual): lower forqstudio-auth-client access-token TTL to
#     60s in Keycloak's admin UI, wait 90s, then run `alcatraz sandbox list`.
#     BearerHandler must call POST /api/v1/auth/refresh and the call must succeed
#     without re-prompting login.

# 14. Tear down
kill $WORKER_PID
docker compose down
```

## Out of scope / deferred

- KRL polling on the client side (gateway/sshd responsibility; KRL endpoint not yet exposed by API).
- The `alcatraz.gateway` itself (planned, not built). The `--no-proxy` switch is forward-compatible: when the gateway ships and the API returns `gatewayPort=443`, the CLI auto-enables the `openssl s_client` ProxyCommand path.
- Multi-profile config (single profile only — no `--profile` flag).
- Telemetry, auto-update, code-signing, package distribution (Homebrew/Scoop/winget/.deb/.rpm).
- `alcatraz sandbox open` (`~/.ssh/config` Match-block alternative to `alcatraz ssh`).
- CI workflow (`dotnet build` + `dotnet test` works locally; CI is a follow-up).
- Worker callback / `Status: Running` transition — sandboxes will read `Provisioning` indefinitely until that ships; the CLI does not block on `Running` because the demo sshd is always up.
- Compose Profiles for opt-in services (e.g. profile-gating Seq or the demo sshd). v1 ships everything always-on. Trivial to add later if the stack grows.
- Containerizing the worker — explicitly host-run for now per the user's instruction. Adding it back to compose is a one-service follow-up if it becomes useful.
