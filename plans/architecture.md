# Alcatraz architecture — shipped decisions

Consolidated record of architecture decisions that are live in the codebase. New design decisions get appended here; speculative work-not-yet-shipped lives in `open-issues.md`.

## Components

| Component | Language | Role |
|---|---|---|
| `alcatraz.api` | .NET 8 | Control plane. Keycloak proxy, sandbox CRUD, SSH CA, NATS publish/consume. |
| `alcatraz.cli` | .NET 8 (Spectre.Console.Cli) | Customer entry point. Device-flow login, sandbox commands, stock-`ssh` wrapper. |
| `alcatraz.worker` | Go 1.25 | Host-privileged process. Spawns/destroys Firecracker VMs, plants files into AgentFS overlay, publishes lifecycle events. |
| `alcatraz.routes` | Go 1.25 | Subscribes to NATS, writes Traefik dynamic-file config keyed by sandbox UUID (SNI). |
| `alcatraz.core` | bash | Kernel + rootfs build. Bakes CA-trust sshd config; init runs `tini → sshd`. |

## Customer SSH access — SSH CA + per-sandbox principals + SNI gateway

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

The API and worker are strangers; they exchange only NATS messages with snake_case JSON payloads.

| Subject | Publisher | Consumer queue group | Payload | Purpose |
|---|---|---|---|---|
| `vm.spawn` | API (outbox) | `worker-vm-spawn` | `{id, vcpus, memory_mib, customer_id}` | Request VM spawn |
| `vm.destroy` | API (outbox) | `worker-vm-destroy` | `{id}` | Request VM teardown |
| `vm.ready` | Worker | `api-vm-ready`, plus fanout (no group) for `alcatraz.routes` | `{id, host, port}` | VM is up and sshd responding (200ms TCP probe loop, 10s budget) |
| `vm.destroyed` | Worker | `api-vm-destroyed`, plus fanout for `alcatraz.routes` | `{id}` | VM exited (post-`m.Wait`, after CNI cleanup + slot release) |

Queue-group convention: `<consumer>-<subject>`. `alcatraz.routes` uses no queue group so every replica builds full state.

Sandbox lifecycle states: `Provisioning(1) → Running(2) → Deleting(3) → Deleted(4)` plus `Failed(5)`. `MarkRunning` requires `Provisioning`; `MarkDestroyed` is idempotent on terminal states. `vm.destroyed` on `Provisioning|Running` → `Failed` (unexpected exit); on `Deleting` → `Deleted`.

## API ↔ CLI auth (device flow proxied through API)

CLI never knows Keycloak realm/client_id/client_secret. Three anonymous endpoints on the API mirror Keycloak's device flow:

| Endpoint | Purpose |
|---|---|
| `POST /api/v1/auth/device` | Initiate; returns `device_code, user_code, verification_uri, verification_uri_complete, expires_in, interval` |
| `POST /api/v1/auth/device/token` | Poll; `authorization_pending` / `slow_down` / `expired_token` / `access_denied` map to RFC 8628 codes via ProblemDetails extension `error` |
| `POST /api/v1/auth/refresh` | Silent token refresh; CLI's `BearerHandler` calls it on proactive refresh (within 60s of expiry) and on `401` |

Tokens cached at `~/.config/alcatraz/tokens.json` (mode `0600`). Workstation ed25519 keypair at `~/.config/alcatraz/id_alcatraz` is generated on first cert request and reused.

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

Worker opens the overlay between `agentfs.PrepareOverlay` and `m.Start(ctx)`, writes:
- `/etc/ssh/auth_principals/al` ← `<sandbox-id>\n` (mode 0644)
- `/etc/ssh/trusted_user_ca_keys` ← contents of `WORKER_CA_PUBKEY_PATH` (default `/run/alcatraz-ca/alcatraz_ca.pub`)

Both writes happen before sshd starts. Failure aborts the spawn; the overlay DB is wiped on the failure path so partial writes vanish.

## Rootfs init (PID 1)

`alcatraz.core/build-rootfs.sh` installs `tini` and templates `/init` to do mounts + ephemeral host-key generation, then `exec /usr/bin/tini -- /usr/sbin/sshd -D -o HostKey=...`. tini reaps zombies and forwards signals; sshd's lifetime *is* the VM's lifetime — if it exits, the kernel panics (`panic=1 reboot=k`), worker observes the exit and publishes `vm.destroyed`.

`sshd_config.d/alcatraz.conf` ships with `TrustedUserCAKeys`, `AuthorizedPrincipalsFile /etc/ssh/auth_principals/%u`, `PasswordAuthentication no`.

The live `alcatraz.core/rootfs/` tree is gitignored — `build-rootfs.sh` is the source of truth; a fresh clone gets the correct rootfs by running it.

## User registration is idempotent against Keycloak

`POST /users/register` reconciles the orphan case (Keycloak has the user but the local `users` row was wiped). On Keycloak `409`, the API queries `GET /users?email=&exact=true` for the existing `identityId`, then either returns the matching local row or inserts a fresh one with that `identityId`. `AdminAuthorizationDelegatingHandler` no longer calls `EnsureSuccessStatusCode` — callers check status themselves; the handler's contract is documented in its XML doc.

## Local dev orchestration — single root `docker-compose.yml`

One compose file at the repo root. Services: `alcatraz.api`, `alcatraz-db` (Postgres 16), `alcatraz-idp` (Keycloak 25, port 8082), `alcatraz-redis`, `alcatraz-nats`, `alcatraz-seq`, `alcatraz-ca-init` (one-shot), `alcatraz-demo-sshd` (stand-in VM for cert pipeline tests, port 2222). Volumes: `keycloak_data`, `alcatraz_ca`. Image tags pinned to majors.

Excluded from compose:
- `alcatraz.worker` — host-run via `sudo -E ./bin/alcatraz-worker` (needs KVM + CNI + root).
- `alcatraz.cli` — built locally with `dotnet`.

Production-only services (under `gateway` compose profile):
- `alcatraz-traefik` — `network_mode: host` so it can reach the worker's `alcatraz0` bridge (`172.16.0.0/24`).
- `alcatraz-routes` — writes `/etc/traefik/dynamic/sandboxes.yml` from `vm.ready`/`vm.destroyed`.

EF migrations apply on API startup in Development.

## Devcontainer scope: code + build only

`.devcontainer/` builds a non-privileged Ubuntu 24.04 image with `dotnet-sdk-8.0`, Go 1.25, kernel/rootfs build prereqs, and the standard CLI shellcheck/jq tooling. No KVM passthrough, no Docker-in-Docker, no firecracker binary. `postCreate.sh` runs the cheap stuff (`dotnet restore` × 2, `go mod download` × 2) and prints a banner pointing at the host-side commands for kernel build, rootfs build, `docker compose up`, and the worker — all of which need host privileges and stay on the host. Heavy/privileged tasks deliberately fail inside the container with a clean error.

## Locked-in parameters

- **Cert TTL:** 24h. KRL not yet shipped; TTL is the revocation primitive.
- **Cert principal:** per-sandbox UUID. Username is always `al`.
- **CA algorithm:** Ed25519. Rotation via dual-CA window (not yet rehearsed; runbook is open work).
- **VM subnet:** `172.16.0.0/24` per worker, hardcoded in `cni/alcatraz-bridge.conflist`. Single-host only — multi-host needs per-host /24 carving.
- **Sandbox lifecycle:** `Provisioning → Running → Deleting → Deleted`, plus `Failed` for unexpected exits.
- **Owner key in domain:** local `users.id` Guid. Keycloak `sub` is read from `IUserContext` only at cert-signing time (for `key_id`).

## Reused libraries / patterns

- Outbox + `IDomainEvent` dispatch: `ProcessOutboxMessagesJob` with `TypeNameHandling.All` on the JSON serializer (writer + reader).
- NATS: `NATS.Net` (singleton `NatsConnectionFactory`) on the .NET side; `nats.go` on the Go side. Snake_case JSON via `JsonNamingPolicy.SnakeCaseLower`.
- HTTP client: typed `IAlcatrazApiClient` + `BearerHandler : DelegatingHandler` on the CLI; auth-anonymous calls flag with `HttpRequestOptionsKey<bool>("anon")`.
