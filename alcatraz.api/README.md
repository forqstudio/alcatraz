# alcatraz.api

The customer-facing control plane for [Alcatraz](../README.md): device-flow login, sandbox lifecycle, SSH CA.

---

## Where this sits in Alcatraz

`alcatraz.cli` talks to this API; this API talks to Keycloak (for identity) and to NATS (to ask `alcatraz.worker` to spawn or destroy a Firecracker VM, and to consume `vm.ready` so the sandbox can transition to `Running`). It signs short-lived OpenSSH user certificates that the in-VM `sshd` accepts; the public TLS path is fronted by Traefik (configured by `alcatraz.routes`).

```
alcatraz.cli ──┬─→ POST /auth/device                  ──→ alcatraz.api ──→ Keycloak (device flow proxy)
               ├─→ POST /sandboxes                    ──→ alcatraz.api ──→ NATS vm.spawn ──→ alcatraz.worker ──→ Firecracker VM (alcatraz.core)
               ├─→ GET  /sandboxes/{id}               ──→ alcatraz.api  (CLI polls until Status == Running)
               │                                                  ▲
               │                                          NATS    │
               │                                          vm.ready│
               │                                                  │ alcatraz.worker
               ├─→ POST /sandboxes/{id}/ssh-cert      ──→ alcatraz.api  (SSH CA signs cert; cert response carries Traefik or per-sandbox endpoint)
               └─→ ssh -o ProxyCommand=openssl s_client …                ──→ Traefik (TLS, SNI) ──→ sshd in VM
                                                                                                   (TrustedUserCAKeys = api's CA pubkey)
```

This API never holds a customer's SSH private key, never sees raw Keycloak credentials from the CLI, and never spawns VMs itself — those are the IdP's and the worker's jobs respectively.

---

## What this component owns

- The `Sandbox` aggregate and its lifecycle (`Provisioning → Running → Deleting → Deleted/Failed`). Cert issuance requires `Running`, which is set by the `VmReadyConsumer` hosted service when the worker publishes `vm.ready`.
- The OAuth 2.0 device-authorization-grant proxy, so the CLI never sees realm, client_id, or client_secret.
- The SSH certificate authority — 24h user certs, principal = sandbox UUID, signed by shelling out to `ssh-keygen -s`. Cert responses carry the routable endpoint: `Gateway:Host/Port` if configured (production via Traefik), otherwise the per-sandbox host/port the worker reported.
- User registration and the role/permission model (Keycloak owns identity; the app DB owns authorization).
- The transactional outbox that turns `SandboxRequested` / `SandboxDeletionRequested` domain events into NATS publishes.

## What this component does NOT own

- VM scheduling, networking, AgentFS — that's [`alcatraz.worker`](../alcatraz.worker/README.md) (NATS subscriber/publisher) plus [`alcatraz.core`](../alcatraz.core/README.md) (kernel + rootfs + Firecracker scripts).
- Customer→VM TLS ingress and SSH multiplexing — that's Traefik (off-the-shelf) fed by [`alcatraz.routes`](../alcatraz.routes/README.md), a small NATS→file-config publisher. The API never directly talks to Traefik or to the worker.
- Identity storage — Keycloak does. This API stores only the local `users` row keyed by Keycloak's `sub`.

---

## Endpoints

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/api/v1/auth/device` | anon | Initiate device flow |
| POST | `/api/v1/auth/device/token` | anon | Poll/exchange for access token |
| POST | `/api/v1/sandboxes` | bearer | Request a new sandbox (publishes to `vm.spawn`) |
| GET  | `/api/v1/sandboxes` | bearer | List caller's sandboxes |
| GET  | `/api/v1/sandboxes/{id}` | bearer | Owner-scoped lookup (404 on miss *or* not-yours) |
| DELETE | `/api/v1/sandboxes/{id}` | bearer | Mark deleting (publishes to `vm.destroy`) |
| POST | `/api/v1/sandboxes/{id}/ssh-cert` | bearer | Sign a short-lived user cert for the caller's pubkey |
| POST | `/api/v1/users/register`, `/api/v1/users/login` | anon | User registration + password-grant login (bootstrap path; CLI uses device flow instead). Register is **idempotent** — a duplicate email returns the existing user id, and a missing local DB row is reconciled against the existing Keycloak identity. |

For the design rationale and the full request lifecycle, see [`docs/customer-cli-and-sandboxes.md`](docs/customer-cli-and-sandboxes.md).

---

## Architecture

**Clean Architecture.** Four projects: `Domain → Application → Infrastructure → Api`. Dependencies point inward. Conventions are codified in [`.claude/CLAUDE.md`](.claude/CLAUDE.md) — read that before touching code.

**CQRS via MediatR.** Commands and queries are records; each has its own handler and FluentValidation validator. Pipeline behaviors handle validation, logging, and unit-of-work scoping.

**Result pattern.** Business-rule violations return `Result.Failure(error)` with a typed `Error` record. No exceptions for expected failure paths. Controllers map errors to HTTP responses via `ResultExtensions.ToFailureActionResult()`.

**Transactional outbox.** Domain events raised inside aggregates are persisted as outbox rows in the same DB transaction as the aggregate change. A Quartz background job drains the outbox and dispatches to handlers, which is where NATS publishes happen. Details in [`docs/outbox-pattern.md`](docs/outbox-pattern.md).

**Authn/authz pipeline.** Keycloak issues JWTs; ASP.NET Core's JWT Bearer middleware validates them; an `IClaimsTransformation` enriches the principal with DB-managed roles; `[HasPermission("...")]` on endpoints triggers a dynamic policy that's evaluated by `PermissionAuthorizationHandler`. Code-level walkthrough in [`docs/keycloak-auth-pipeline.md`](docs/keycloak-auth-pipeline.md); long-form reference in [`docs/authentication-authorization.md`](docs/authentication-authorization.md).

---

## Tech stack

| Concern | Technology |
|---|---|
| Runtime | .NET 8, ASP.NET Core |
| Data | PostgreSQL via EF Core, Dapper for read-heavy queries |
| Cache | Redis (StackExchange.Redis) |
| Identity | Keycloak (JWT Bearer + OAuth 2.0 device flow) |
| Messaging | NATS.Net (publishes `vm.spawn`, `vm.destroy`; consumes `vm.ready` and `vm.destroyed` via `BackgroundService`s) |
| CQRS / mediator | MediatR |
| Validation | FluentValidation |
| Background jobs | Quartz.NET (outbox drain) |
| Logging | Serilog → console + Seq |
| SSH CA | OpenSSH `ssh-keygen` (subprocess) |
| Testing | xUnit, NSubstitute, FluentAssertions, Testcontainers |

---

## Running locally

Local development is orchestrated by the repo-root [`docker-compose.yml`](../docker-compose.yml) — there's no per-component compose anymore.

```bash
cd ..                       # from alcatraz.api/, go up to the repo root
docker compose up -d --build
docker compose logs -f alcatraz.api  # wait for "Now listening on: http://[::]:8080"
```

This brings up the API plus Keycloak, Postgres, Redis, NATS, Seq, and a one-shot container that generates the SSH CA into a shared volume. `alcatraz.worker` runs on the host (out of compose) and connects to NATS at `localhost:4222`; `alcatraz.cli` is built locally with `dotnet`. Two optional profiles: `--profile gateway` (Traefik + `alcatraz.routes` for public TLS ingress) and `--profile demo-sshd` (legacy Alpine sshd stand-in for cert-pipeline testing without a worker).

For the full register → device login → create sandbox → fetch cert → SSH walkthrough, see [`docs/local-end-to-end.md`](docs/local-end-to-end.md).

---

## Project structure

```
src/
├── Alcatraz.Domain/             # aggregates (Sandbox, User), domain events, errors, repository interfaces
├── Alcatraz.Application/        # use cases — commands, queries, validators, handlers
│   ├── Auth/                      # InitiateDeviceAuth, ExchangeDeviceToken
│   ├── Sandboxes/                 # CreateSandbox, DeleteSandbox, GetSandbox, ListSandboxes, IssueSshCertificate, MarkSandboxRunning
│   └── Users/                     # Register, Login, GetLoggedInUser
├── Alcatraz.Infrastructure/     # EF Core, Keycloak clients, NATS publisher, ssh-keygen CA, Quartz outbox
└── Alcatraz.Api/                # controllers, middleware, DI composition, appsettings
test/
├── Alcatraz.Domain.UnitTests/
├── Alcatraz.Application.UnitTests/
├── Alcatraz.Application.IntegrationTests/   # real Postgres via Testcontainers
└── Alcatraz.Api.FunctionalTests/            # WebApplicationFactory + real Keycloak
```

---

## Deeper docs

- [`docs/customer-cli-and-sandboxes.md`](docs/customer-cli-and-sandboxes.md) — sandbox feature design, NATS contract, SSH CA mechanics
- [`docs/local-end-to-end.md`](docs/local-end-to-end.md) — curl walkthrough end-to-end against `docker compose`
- [`docs/keycloak-auth-pipeline.md`](docs/keycloak-auth-pipeline.md) — code-level tour of the Keycloak / JWT / `[HasPermission]` pipeline
- [`docs/authentication-authorization.md`](docs/authentication-authorization.md) — long-form authn/authz reference
- [`docs/outbox-pattern.md`](docs/outbox-pattern.md) — outbox semantics and Quartz wiring
- [`../plans/customer-vm-access-ssh-ca.md`](../plans/customer-vm-access-ssh-ca.md) — system-level SSH CA + device flow + gateway design
- [`../plans/alcatraz-api-cli-endpoints.md`](../plans/alcatraz-api-cli-endpoints.md) — endpoint spec this README mirrors
- [`.claude/CLAUDE.md`](.claude/CLAUDE.md) — coding conventions enforced in this codebase

---

## Legacy

The `Bookings/` and `Apartments/` aggregates under `src/` are scaffolding from the project this codebase forked from. They are scheduled for removal and are not part of Alcatraz's domain. Ignore them when reasoning about endpoints or data; sandbox is the real domain.
