# Alcatraz architecture — shipped decisions

Consolidated record of architecture decisions that are live in the codebase. New design decisions get appended here; speculative work-not-yet-shipped lives in `open-issues.md`.

## Context — what Alcatraz is

Alcatraz is a **paid, multi-tenant serverless sandbox** for AI coding agents. A customer signs in from their workstation, asks for a sandbox, and gets a Firecracker microVM they can SSH into with stock OpenSSH. Each sandbox is a throwaway Linux box: hardware-isolated via KVM, reachable over a short-lived certificate, and disposable after use.

The customer is **external** — a paying user, not a teammate — so every design choice is shaped by adversarial assumptions about what one tenant might do to another, and by the operational reality that the operator can't be in the loop for individual SSH sessions.

## Hard constraints driving the design

These are the load-bearing requirements. Almost every decision below traces back to one of them:

1. **Each customer sees only their own VM.** From the customer's perspective the worker host does not exist. They never resolve a worker hostname, never connect to a worker IP, never share a TCP session that reveals one.
2. **Customer ↔ customer isolation.** Customer A must never reach customer B's VM, even on a misconfigured network. Defence applies at network, transport, and discovery layers.
3. **No long-term storage of customer SSH pubkeys.** Auth uses an SSH CA + identity provider, not a persisted pubkey registry. Nothing sensitive about customer keys is held at rest.
4. **Frictionless client.** Customers use stock `ssh` — no WireGuard install, no proprietary client. The CLI does device-flow login and cert fetch, then execs OpenSSH.
5. **Operator-friendly fleet.** Multi-host worker pool driven by NATS; control plane scales independently of host capacity; everything observable is observable through the same outbox/messaging seams.
6. **Failure isolation across components.** No single component compromise yields VM access. The VM's own sshd is the cryptographic source of truth; gateway/API/worker are convenience layers around it.

## Components

| Component | Language | Role |
|---|---|---|
| `alcatraz.api` | .NET 8 | Control plane. Keycloak proxy, sandbox CRUD, SSH CA, NATS publish/consume. |
| `alcatraz.cli` | .NET 8 (Spectre.Console.Cli) | Customer entry point. Device-flow login, sandbox commands, stock-`ssh` wrapper. |
| `alcatraz.worker` | Go 1.25 | Host-privileged process. Spawns/destroys Firecracker VMs, plants files into AgentFS overlay, publishes lifecycle events. |
| `alcatraz.routes` | Go 1.25 | Subscribes to NATS, writes Traefik dynamic-file config keyed by sandbox UUID (SNI). |
| `alcatraz.core` | bash | Kernel + rootfs build. Bakes CA-trust sshd config; init runs `tini → sshd`. |

## `alcatraz.api` internal architecture — DDD, CQRS, outbox

**Why:** the API has heavy domain logic (sandbox lifecycle, role/permission model, cert issuance) *and* a wide read surface (list/get for the CLI), *and* it has to publish NATS messages atomically with DB writes. DDD concentrates business rules in the aggregate where they're testable without infrastructure; CQRS keeps read paths free to use Dapper without polluting write paths; the transactional outbox makes "the row exists ⇔ the message was published" a database invariant rather than a runtime hope.

### Layers (Clean Architecture, dependencies point inward)

- **Domain** (`Alcatraz.Domain`): aggregates (`Sandbox`, `User`, `Role`, `Permission`), value objects, domain events, repository interfaces, error catalogues. No external dependencies.
- **Application** (`Alcatraz.Application`): commands + queries via MediatR, FluentValidation validators, infrastructure abstractions (e.g. `ISshCertificateAuthority`, `ISandboxEventPublisher`, `IDeviceAuthorizationClient`).
- **Infrastructure** (`Alcatraz.Infrastructure`): EF Core configurations + repositories, NATS publish/subscribe, Keycloak OIDC + admin clients, `ssh-keygen` shell-out.
- **API** (`Alcatraz.Api`): controllers (primary constructors, `ISender`), permission attributes, ProblemDetails middleware.

### DDD + CQRS conventions

- **Aggregates** (`sealed class : Entity`): private parameterised + parameterless (EF) constructors, private setters, static factory methods (`Sandbox.Request`), state-transition methods that return `Result` and `RaiseDomainEvent` on success.
- **Result pattern**: business-rule violations return `Result.Failure(error)`; never throw. Errors are static instances on `*Errors` classes (e.g. `SandboxErrors.NotFound`, `SandboxErrors.AlreadyDeleting`). Controllers map `IsSuccess`/`IsFailure` to HTTP status.
- **Commands** (`record : ICommand<T>`) paired with `*CommandHandler` and `*CommandValidator`. Validators run via a MediatR pipeline behaviour before the handler.
- **Queries** (`record : IQuery<T>`) return DTOs, not domain entities. Read-heavy ones use Dapper directly; cacheable ones implement `ICachedQuery`.
- **Feature folders**: grouped by aggregate + use case (`Application/Sandboxes/CreateSandbox/{Command,Handler,Validator}.cs`), not by technical concern.
- **`IDateTimeProvider`** for `utcNow` injection — domain logic stays deterministic in tests.

### Transactional outbox (atomic domain events → NATS)

The spawn message has to be published exactly when the sandbox row exists — never before, never without. Direct publish-from-handler loses messages if NATS is down and double-publishes if the DB transaction rolls back after a successful publish. Outbox flow:

1. Aggregate raises a domain event (e.g. `Sandbox.Request` raises `SandboxRequestedDomainEvent`).
2. EF `SaveChangesAsync` interceptor serialises the event into `outbox_messages` with `TypeNameHandling.All` (so polymorphic `IDomainEvent` round-trips), in the same transaction as `INSERT INTO sandboxes`.
3. `ProcessOutboxMessagesJob` (Quartz) polls unprocessed rows, dispatches each via MediatR to its `*DomainEventHandler`, which calls into infrastructure (`ISandboxEventPublisher.PublishSpawnAsync` → NATS).
4. Successful dispatch sets `processed_on_utc`. At-least-once: handlers must be idempotent (the API's `vm.ready` consumer is idempotent on `Running`; the worker's spawn handler is keyed on sandbox UUID).

Trade-offs:
- Latency: spawn is published ~1s after the API responds 201, not synchronously. Acceptable because the CLI polls until `Running`.
- Today's failure handling is coarse: a failed dispatch still marks the row processed (see [`open-issues.md`](open-issues.md) — `[P0]` outbox swallows failures). Fix is retry counter + reaper, not a redesign.

## Customer SSH access — SSH CA + per-sandbox principals + SNI gateway

**Why:** satisfies constraints 1, 2, 3, 4, 6 — the entire customer-facing surface. A short-lived per-sandbox certificate plus per-VM `auth_principals` gives cryptographic per-tenant isolation that survives a compromised gateway or API. The SNI-as-routing-key gateway hides worker identity behind a single public TLS endpoint. Stock `ssh` keeps the client side dependency-free.

**Goal:** customer's stock `ssh` lands in their Firecracker VM, with cryptographic isolation from other customers and zero persisted customer pubkeys.

**Trust chain:**
- `alcatraz.api` operates an SSH CA. Private key never leaves the API; only the public key is distributed (baked into the rootfs at build time, written into each VM's overlay at spawn).
- Customer authenticates via OAuth 2.0 device-code flow against self-hosted Keycloak. The API proxies the device flow so the CLI never holds Keycloak's `client_secret`.
- Per SSH session: CLI generates an ed25519 workstation keypair on first use, POSTs the pubkey + bearer token to `/api/v1/sandboxes/{id}/ssh-cert`, gets back an OpenSSH user cert with `principal = <sandbox-id>`, `valid_before = now+24h`, `key_id = <identityId>:<sandboxId>:<unixTs>`. Pubkey is **never persisted** by the API.
- VM's sshd has `TrustedUserCAKeys /etc/ssh/trusted_user_ca_keys` and `AuthorizedPrincipalsFile /etc/ssh/auth_principals/%u`. The worker writes a single line — the sandbox UUID — into `auth_principals/al` inside the AgentFS overlay before boot. sshd accepts only certs whose principal matches that one UUID.
- SSH user is always `al`; principal scoping happens via `auth_principals`, not via the username.

**Public ingress (production profile):** single TLS endpoint `ssh.alcatraz.io:443`. Traefik terminates TLS, matches SNI = sandbox UUID against a per-sandbox TCP router, splices bytes to `<vm_ip>:22`. CLI invokes stock `ssh` with `ProxyCommand="openssl s_client -quiet -connect ssh.alcatraz.io:443 -servername <id>"`. ACME via TLS-ALPN-01 (single cert for `ssh.alcatraz.io`, shared across routers).

**Local dev (no `gateway` profile):** API config leaves `Gateway:Host` unset, so the cert response carries the per-sandbox VM endpoint (`172.16.0.x:22`); CLI dials directly. Same code path, one config flip.

**Failure isolation:** gateway compromise alone does not yield VM access — the VM's sshd independently verifies the cert against its baked-in CA pubkey + the per-VM principals file.

## API ↔ Worker integration (NATS)

**Why:** constraint 5. The API runs in containers (no KVM, no root); the worker runs on bare hosts (KVM, CNI, root). Coupling them only through NATS subjects means the control plane can scale, redeploy, and fail independently of host capacity. The transactional outbox guarantees a `vm.spawn` is published exactly when a sandbox row exists — no lost spawns, no double spawns. Queue groups give us competing consumers for spawn/destroy work, and fanout (no group) for routes-table replicas.

The API and worker are strangers; they exchange only NATS messages with snake_case JSON payloads.

| Subject | Publisher | Consumer queue group | Payload | Purpose |
|---|---|---|---|---|
| `vm.spawn` | API (outbox) | `worker-vm-spawn` | `{id, vcpus, memory_mib, customer_id}` | Request VM spawn |
| `vm.destroy` | API (outbox) | `worker-vm-destroy` | `{id}` | Request VM teardown |
| `vm.ready` | Worker | `api-vm-ready`, plus fanout (no group) for `alcatraz.routes` | `{id, host, port}` | VM is up and sshd responding (200ms TCP probe loop, 10s budget) |
| `vm.destroyed` | Worker | `api-vm-destroyed`, plus fanout for `alcatraz.routes` | `{id}` | VM exited (post-`m.Wait`, after CNI cleanup + slot release) |

Queue-group convention: `<consumer>-<subject>`. `alcatraz.routes` uses no queue group so every replica builds full state.

Sandbox lifecycle states: `Provisioning(1) → Running(2) → Deleting(3) → Deleted(4)` plus `Failed(5)`. `MarkRunning` requires `Provisioning`; `MarkDestroyed` is idempotent on terminal states. `vm.destroyed` on `Provisioning|Running` → `Failed` (unexpected exit); on `Deleting` → `Deleted`.

## Identity provider — Keycloak

**Why:** offloading customer identity to a hardened IdP means the API never sees passwords, never reimplements MFA, and gets a battle-tested OIDC implementation including the device authorization grant (RFC 8628) the CLI needs. Self-hosting (vs. a SaaS IdP) keeps the auth perimeter inside our trust boundary and avoids per-user vendor cost. Customer authentication, password hashing, MFA, federation, and account lockout are entirely Keycloak's job — the API only validates JWTs and reads claims.

Realm: `alcatraz`, with the confidential OIDC client `alcatraz-auth-client`. Device authorization grant enabled. The realm export at `alcatraz.api/.files/alcatraz-realm-export.json` is the source of truth for local dev (Keycloak imports it on first boot; a wipe-and-reimport drift is currently unguarded — see open issues). The API caches Keycloak's JWKS for bearer-token verification (`iss`, `aud`, `exp`, signature).

Two integration patterns the API uses:

### 1. Device-flow proxy (CLI → API → Keycloak)

The CLI is distributed to customers, so it can't hold Keycloak's `client_secret` (constraint 4). Proxying the device flow through the API means we can rotate Keycloak realm/client config without re-shipping the CLI, and the CLI binary contains no privileged credentials to extract.

Three anonymous endpoints on the API mirror Keycloak's device flow:

| Endpoint | Purpose |
|---|---|
| `POST /api/v1/auth/device` | Initiate; returns `device_code, user_code, verification_uri, verification_uri_complete, expires_in, interval` |
| `POST /api/v1/auth/device/token` | Poll; `authorization_pending` / `slow_down` / `expired_token` / `access_denied` map to RFC 8628 codes via ProblemDetails extension `error` |
| `POST /api/v1/auth/refresh` | Silent token refresh; CLI's `BearerHandler` calls it on proactive refresh (within 60s of expiry) and on `401` |

`KeycloakDeviceAuthorizationClient` is the typed `HttpClient` that wraps these calls; it forwards Keycloak's `error` field verbatim so RFC 8628 semantics survive the proxy. CLI tokens cache at `~/.config/alcatraz/tokens.json` (mode `0600`); the workstation ed25519 keypair at `~/.config/alcatraz/id_alcatraz` is generated on first cert request and reused.

### 2. Admin API for user registration (API → Keycloak admin)

`POST /api/v1/users/register` creates the user in Keycloak via its admin REST API, then mirrors a row into the local `users` table keyed on Keycloak's `sub` (`identity_id`). The admin client uses a service-account token attached by `AdminAuthorizationDelegatingHandler`. Splitting it this way means: Keycloak owns the credential (we never store passwords); the local `users` row owns the foreign-key target for sandbox ownership and the role/permission edges; the two are reconciled on `409` so the local DB can be wiped without locking customers out (detailed below in § User registration).

`AdminAuthorizationDelegatingHandler` no longer calls `EnsureSuccessStatusCode` — callers check status themselves so `AuthenticationService` can branch on Keycloak's 409. The handler's contract is documented in its XML doc; only one client (`AuthenticationService`) is registered against it today, and any future registration must remember to check status itself.

### Authn/authz on protected endpoints

JWT bearer middleware verifies Keycloak-issued tokens. A claims transformation joins the user's roles → permissions on every request. The custom `[HasPermission(...)]` attribute gates controllers at the permission level (not the role level — roles are an indirection so permission edges can be edited without re-issuing tokens). `IUserContext` exposes the resolved local `UserId` + Keycloak `IdentityId` to handlers; sandbox ownership uses `UserId`, while the SSH cert's `key_id` includes `IdentityId` for cross-system audit.

## Sandbox CRUD endpoints

| Method | Path | Auth | Notes |
|---|---|---|---|
| POST | `/api/v1/sandboxes` | bearer | `{vcpus 1..16, memoryMib 512..32768 step 256}`. 201. Raises `SandboxRequestedDomainEvent` → outbox → `vm.spawn`. |
| GET | `/api/v1/sandboxes` | bearer | Owner-scoped, excludes Deleted. |
| GET | `/api/v1/sandboxes/{id}` | bearer | Owner-scoped; not-yours = 404, never leak existence. |
| DELETE | `/api/v1/sandboxes/{id}` | bearer | 202; raises `SandboxDeletionRequestedDomainEvent` → outbox → `vm.destroy`. |
| POST | `/api/v1/sandboxes/{id}/ssh-cert` | bearer | `{sshPubkey}` → `{cert, validUntilUtc, gatewayHost, gatewayPort}`. Requires `Status == Running`. |

Cert signing shells out to `ssh-keygen -s` (`openssh-client` in the API container).

## AgentFS overlay writes (pre-boot)

**Why:** constraints 1, 2, 6. The per-VM principal scope (`auth_principals/al = <sandbox-uuid>`) and the trusted CA pubkey have to be in place before sshd accepts its first connection — there's no out-of-band provisioning channel into a customer-facing VM. Writing them into the overlay before `m.Start()` is the latest possible moment that's still atomic with the spawn. Failure aborts the spawn so a half-trusted VM never reaches `Running`.

Worker opens the overlay between `agentfs.PrepareOverlay` and `m.Start(ctx)`, writes:
- `/etc/ssh/auth_principals/al` ← `<sandbox-id>\n` (mode 0644)
- `/etc/ssh/trusted_user_ca_keys` ← contents of `WORKER_CA_PUBKEY_PATH` (default `/run/alcatraz-ca/alcatraz_ca.pub`)

Both writes happen before sshd starts. Failure aborts the spawn; the overlay DB is wiped on the failure path so partial writes vanish.

## Rootfs init (PID 1)

**Why:** clean failure semantics. The customer's whole reason for the VM existing is sshd; tying sshd's lifetime to the VM's lifetime means there's no "running but broken" state. If sshd dies, the kernel panics, the worker observes the exit, and the API transitions the sandbox to `Failed` — no silent half-up VMs that pass the readiness probe but reject SSH.

`alcatraz.core/build-rootfs.sh` installs `tini` and templates `/init` to do mounts + ephemeral host-key generation, then `exec /usr/bin/tini -- /usr/sbin/sshd -D -o HostKey=...`. tini reaps zombies and forwards signals; sshd's lifetime *is* the VM's lifetime — if it exits, the kernel panics (`panic=1 reboot=k`), worker observes the exit and publishes `vm.destroyed`.

`sshd_config.d/alcatraz.conf` ships with `TrustedUserCAKeys`, `AuthorizedPrincipalsFile /etc/ssh/auth_principals/%u`, `PasswordAuthentication no`.

The live `alcatraz.core/rootfs/` tree is gitignored — `build-rootfs.sh` is the source of truth; a fresh clone gets the correct rootfs by running it.

## User registration is idempotent against Keycloak

**Why:** dev-cycle reality. Keycloak and the API DB are separate volumes; either can be wiped independently. Without reconciliation, a wiped API DB locks the customer out (Keycloak still has them, but the local row is missing). Idempotent reconciliation makes registration a recovery operation as well as a creation operation, with no admin intervention.

`POST /users/register` reconciles the orphan case (Keycloak has the user but the local `users` row was wiped). On Keycloak `409`, the API queries `GET /users?email=&exact=true` for the existing `identityId`, then either returns the matching local row or inserts a fresh one with that `identityId`. `AdminAuthorizationDelegatingHandler` no longer calls `EnsureSuccessStatusCode` — callers check status themselves; the handler's contract is documented in its XML doc.

## Local dev orchestration — single root `docker-compose.yml`

**Why:** prior split (one compose per component) had overlapping NATS + Seq services and conflicting host ports — running them simultaneously was broken. One root compose is one source of truth for local infra, and matches the deployment topology (single host, multiple services). Profiles let local dev skip Traefik/`alcatraz.routes` without a flag.

One compose file at the repo root. Services: `alcatraz.api`, `alcatraz-db` (Postgres 16), `alcatraz-idp` (Keycloak 25, port 8082), `alcatraz-redis`, `alcatraz-nats`, `alcatraz-seq`, `alcatraz-ca-init` (one-shot), `alcatraz-demo-sshd` (stand-in VM for cert pipeline tests, port 2222). Volumes: `keycloak_data`, `alcatraz_ca`. Image tags pinned to majors.

Excluded from compose:
- `alcatraz.worker` — host-run via `sudo -E ./bin/alcatraz-worker` (needs KVM + CNI + root).
- `alcatraz.cli` — built locally with `dotnet`.

Production-only services (under `gateway` compose profile):
- `alcatraz-traefik` — `network_mode: host` so it can reach the worker's `alcatraz0` bridge (`172.16.0.0/24`).
- `alcatraz-routes` — writes `/etc/traefik/dynamic/sandboxes.yml` from `vm.ready`/`vm.destroyed`.

EF migrations apply on API startup in Development.

## Devcontainer scope: code + build only

**Why:** the heavy/privileged steps (kernel build, rootfs build, `docker compose up`, the worker) genuinely need host privileges — KVM passthrough, root, bridge namespace — and trying to host them inside the devcontainer means either dropping `--privileged` (broken) or shipping a privileged container (foot-gun). Drawing the scope at "code + build" matches what a teammate actually wants from a devcontainer (toolchains, extensions, fast restore) and lets the host workflow stay simple.

`.devcontainer/` builds a non-privileged Ubuntu 24.04 image with `dotnet-sdk-8.0`, Go 1.25, kernel/rootfs build prereqs, and the standard CLI shellcheck/jq tooling. No KVM passthrough, no Docker-in-Docker, no firecracker binary. `postCreate.sh` runs the cheap stuff (`dotnet restore` × 2, `go mod download` × 2) and prints a banner pointing at the host-side commands for kernel build, rootfs build, `docker compose up`, and the worker — all of which need host privileges and stay on the host. Heavy/privileged tasks deliberately fail inside the container with a clean error.

## Locked-in parameters

- **Cert TTL:** 24h. KRL not yet shipped; TTL is the revocation primitive.
- **Cert principal:** per-sandbox UUID. Username is always `al`.
- **CA algorithm:** Ed25519. Rotation via dual-CA window (not yet rehearsed; runbook is open work).
- **VM subnet:** `172.16.0.0/24` per worker, hardcoded in `cni/alcatraz-bridge.conflist`. Single-host only — multi-host needs per-host /24 carving.
- **Sandbox lifecycle:** `Provisioning → Running → Deleting → Deleted`, plus `Failed` for unexpected exits.
- **Owner key in domain:** local `users.id` Guid. Keycloak `sub` is read from `IUserContext` only at cert-signing time (for `key_id`).

## Library / wiring conventions

- **MediatR** for command/query dispatch. **FluentValidation** for command validation via a pipeline behaviour. **Quartz** for the outbox drainer. **EF Core** + **Dapper** (write vs. read paths). **Serilog** + **Seq** in dev.
- **NATS**: `NATS.Net` (singleton `NatsConnectionFactory`) on the .NET side; `nats.go` on the Go side. Snake_case JSON via `JsonNamingPolicy.SnakeCaseLower`.
- **CLI HTTP**: typed `IAlcatrazApiClient` + `BearerHandler : DelegatingHandler`; auth-anonymous calls flag with `HttpRequestOptionsKey<bool>("anon")`.
- **Testing** (alcatraz.api): xUnit + NSubstitute + FluentAssertions. Functional tests use `WebApplicationFactory` with substituted infrastructure (`IDeviceAuthorizationClient`, `ISandboxEventPublisher`); the SSH CA path runs the real `ssh-keygen` against a fixture key.
