# Alcatraz architecture — context and decision index

Top-of-file context for the system, plus the index of [Architecture Decision Records](../docs/adr/README.md). Each shipped decision now lives in its own ADR; this document carries only the context that *all* the ADRs depend on, the components map, and the reference material that has no decision rationale of its own.

Speculative work-not-yet-shipped lives in [`open-issues.md`](open-issues.md).

## Context — what Alcatraz is

Alcatraz is a **paid, multi-tenant serverless sandbox** for AI coding agents. A customer signs in from their workstation, asks for a sandbox, and gets a Firecracker microVM they can SSH into with stock OpenSSH. Each sandbox is a throwaway Linux box: hardware-isolated via KVM, reachable over a short-lived certificate, and disposable after use.

The customer is **external** — a paying user, not a teammate — so every design choice is shaped by adversarial assumptions about what one tenant might do to another, and by the operational reality that the operator can't be in the loop for individual SSH sessions.

## Hard constraints driving the design

These are the load-bearing requirements. Almost every ADR below traces back to one of them:

1. **Each customer sees only their own VM.** From the customer's perspective the worker host does not exist. They never resolve a worker hostname, never connect to a worker IP, never share a TCP session that reveals one.
2. **Customer ↔ customer isolation.** Customer A must never reach customer B's VM, even on a misconfigured network. Defence applies at network, transport, and discovery layers.
3. **No long-term storage of customer SSH pubkeys.** Auth uses an SSH CA + identity provider, not a persisted pubkey registry. Nothing sensitive about customer keys is held at rest.
4. **Frictionless client.** Customers use stock `ssh` — no WireGuard install, no proprietary client. The CLI does device-flow login and cert fetch, then execs OpenSSH.
5. **Operator-friendly fleet.** Multi-host worker pool driven by NATS; control plane scales independently of host capacity; everything observable is observable through the same outbox/messaging seams.
6. **Failure isolation across components.** No single component compromise yields VM access. The VM's own sshd is the cryptographic source of truth; gateway/API/worker are convenience layers around it.

## Components

| Component         | Language                       | Role                                                                                                |
| ----------------- | ------------------------------ | --------------------------------------------------------------------------------------------------- |
| `alcatraz.api`    | .NET 8                         | Control plane. Keycloak proxy, sandbox CRUD, SSH CA, NATS publish/consume.                          |
| `alcatraz.cli`    | .NET 8 (Spectre.Console.Cli)   | Customer entry point. Device-flow login, sandbox commands, stock-`ssh` wrapper.                     |
| `alcatraz.worker` | Go 1.25                        | Host-privileged process. Spawns/destroys Firecracker VMs, plants files into AgentFS overlay, publishes lifecycle events. |
| `alcatraz.routes` | Go 1.25                        | Subscribes to NATS, writes Traefik dynamic-file config keyed by sandbox UUID (SNI).                 |
| `alcatraz.core`   | bash                           | Kernel + rootfs build. Bakes CA-trust sshd config; init runs `tini → sshd`.                         |

## Architecture decisions

Detailed rationale for each shipped decision lives in [`../docs/adr/`](../docs/adr/README.md). The index, grouped by area:

### Control plane (`alcatraz.api`)

- [ADR-0002 — Clean Architecture, DDD, CQRS for `alcatraz.api`](../docs/adr/0002-clean-architecture-ddd-cqrs-for-api.md)
- [ADR-0003 — Transactional outbox for atomic domain events → NATS](../docs/adr/0003-transactional-outbox.md)
- [ADR-0006 — Self-hosted Keycloak as identity provider with device-flow proxy](../docs/adr/0006-keycloak-identity-provider.md)
- [ADR-0009 — Idempotent Keycloak user registration](../docs/adr/0009-keycloak-idempotent-user-registration.md)

### Customer-facing surface

- [ADR-0004 — Customer SSH access: SSH CA + per-sandbox principals + SNI gateway](../docs/adr/0004-customer-ssh-access.md)
- [ADR-0007 — AgentFS overlay writes happen pre-boot](../docs/adr/0007-agentfs-overlay-pre-boot.md)
- [ADR-0008 — Rootfs init = `tini → sshd`; sshd lifetime = VM lifetime](../docs/adr/0008-rootfs-init-tini-sshd.md)

### API ↔ worker integration

- [ADR-0001 — Core NATS over JetStream (lifecycle subjects)](../docs/adr/0001-core-nats-over-jetstream.md)
- [ADR-0005 — NATS as the only API ↔ worker coupling](../docs/adr/0005-nats-as-api-worker-coupling.md)
- [ADR-0012 — JetStream for billing subjects](../docs/adr/0012-jetstream-for-billing-subjects.md)
- Reference: [`../docs/nats-broker.md`](../docs/nats-broker.md) — full broker topology, producers, consumers, contracts, configuration.
- Reference: [`../docs/billing-metrics.md`](../docs/billing-metrics.md) — what billing measures, how customers see it, the worker → JetStream → API pipeline.

### Local dev and tooling

- [ADR-0010 — Single root `docker-compose.yml` for local dev orchestration](../docs/adr/0010-single-root-docker-compose.md)
- [ADR-0011 — Devcontainer scope = code + build only](../docs/adr/0011-devcontainer-scope.md)

## Sandbox CRUD endpoints

Reference, not a decision — the API surface that exercises the ADRs above.

| Method | Path                                  | Auth   | Notes                                                                                              |
| ------ | ------------------------------------- | ------ | -------------------------------------------------------------------------------------------------- |
| POST   | `/api/v1/sandboxes`                   | bearer | `{vcpus 1..16, memoryMib 512..32768 step 256}`. 201. Raises `SandboxRequestedDomainEvent` → outbox → `vm.spawn`. |
| GET    | `/api/v1/sandboxes`                   | bearer | Owner-scoped, excludes Deleted.                                                                    |
| GET    | `/api/v1/sandboxes/{id}`              | bearer | Owner-scoped; not-yours = 404, never leak existence.                                               |
| DELETE | `/api/v1/sandboxes/{id}`              | bearer | 202; raises `SandboxDeletionRequestedDomainEvent` → outbox → `vm.destroy`.                         |
| POST   | `/api/v1/sandboxes/{id}/ssh-cert`     | bearer | `{sshPubkey}` → `{cert, validUntilUtc, gatewayHost, gatewayPort}`. Requires `Status == Running`.   |
| GET    | `/api/v1/sandboxes/usage`             | bearer | Owner-scoped finalised usage records; newest first.                                                |
| GET    | `/api/v1/sandboxes/{id}/usage`        | bearer | One sandbox; returns the finalised record if present, otherwise a live in-progress view computed from samples + the current clock. ([ADR-0012](../docs/adr/0012-jetstream-for-billing-subjects.md)) |

Cert signing shells out to `ssh-keygen -s` (`openssh-client` in the API container). See [ADR-0004](../docs/adr/0004-customer-ssh-access.md) for the full trust chain.

## Locked-in parameters

These values are referenced across multiple ADRs; collected here for searchability.

- **Cert TTL:** 24h. KRL not yet shipped; TTL is the revocation primitive. ([ADR-0004](../docs/adr/0004-customer-ssh-access.md))
- **Cert principal:** per-sandbox UUID. Username is always `al`. ([ADR-0004](../docs/adr/0004-customer-ssh-access.md), [ADR-0007](../docs/adr/0007-agentfs-overlay-pre-boot.md))
- **CA algorithm:** Ed25519. Rotation via dual-CA window (not yet rehearsed; runbook is open work).
- **VM subnet:** `172.16.0.0/24` per worker, hardcoded in `cni/alcatraz-bridge.conflist`. Single-host only — multi-host needs per-host /24 carving.
- **Sandbox lifecycle:** `Provisioning → Running → Deleting → Deleted`, plus `Failed` for unexpected exits. `MarkRunning` requires `Provisioning`; `MarkDestroyed` is idempotent on terminal states. `vm.destroyed` on `Provisioning|Running` → `Failed`; on `Deleting` → `Deleted`. ([ADR-0005](../docs/adr/0005-nats-as-api-worker-coupling.md))
- **Owner key in domain:** local `users.id` Guid. Keycloak `sub` is read from `IUserContext` only at cert-signing time (for `key_id`). ([ADR-0006](../docs/adr/0006-keycloak-identity-provider.md))
- **Billing window:** `Sandbox.ReadyAtUtc → DeletedOnUtc` (or `now` while in-flight). Dimensions: provisioned vCPU-s + MiB-s (computed); actual CPU usec (cgroup v2 `cpu.stat`) + net rx/tx bytes (Firecracker `/metrics`). Disk billing deferred. Persistence: `sandbox_usage_records` (one row per finalised sandbox, PK = sandbox_id) and `sandbox_usage_samples` (one row per 60s tick, unique on `(sandbox_id, sampled_at_utc)`). ([ADR-0012](../docs/adr/0012-jetstream-for-billing-subjects.md))
- **Per-VM cgroup isolation:** each Firecracker process is wrapped in `systemd-run --scope --slice=alcatraz.slice --unit=alcatraz-vm-<id>.scope`. Deterministic cgroup path; systemd reaps the scope on exit. ([ADR-0012](../docs/adr/0012-jetstream-for-billing-subjects.md))

## Library / wiring conventions

Reference, not a decision — the libraries each ADR assumes.

- **MediatR** for command/query dispatch. **FluentValidation** for command validation via a pipeline behaviour. **Quartz** for the outbox drainer. **EF Core** + **Dapper** (write vs. read paths). **Serilog** + **Seq** in dev.
- **NATS:** `NATS.Net` (singleton `NatsConnectionFactory`) on the .NET side; `nats.go` on the Go side. Snake_case JSON via `JsonNamingPolicy.SnakeCaseLower`. Full reference: [`../docs/nats-broker.md`](../docs/nats-broker.md).
- **CLI HTTP:** typed `IAlcatrazApiClient` + `BearerHandler : DelegatingHandler`; auth-anonymous calls flag with `HttpRequestOptionsKey<bool>("anon")`.
- **Testing** (`alcatraz.api`): xUnit + NSubstitute + FluentAssertions. Functional tests use `WebApplicationFactory` with substituted infrastructure (`IDeviceAuthorizationClient`, `ISandboxEventPublisher`); the SSH CA path runs the real `ssh-keygen` against a fixture key.
