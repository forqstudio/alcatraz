# Alcatraz: POC → MVP plan

## Where the system actually is

The five components (api, cli, worker, routes, core) all run end-to-end — `alcatraz login` through to `ssh` works against a Firecracker VM. The plumbing that's hard to get right (transactional outbox, idempotent `MarkSandboxRunning`, SNI-as-routing-key, ssh-keygen-CA with sandbox-UUID principals, `auth_principals`/`trusted_user_ca_keys` planted into the overlay pre-boot) is in place, and the per-user ownership check on every sandbox endpoint is wired correctly. The README's "Shipped" list is largely accurate.

**The reason it's not production-ready isn't that the happy path is broken — it's that adversarial paths and operational paths aren't.**

## Why it isn't production-ready

### Tenant isolation is incomplete (the existential issue for a paid product)

1. **VMs on the same worker can reach each other.** The worker itself documents this in `alcatraz.worker/docs/network-isolation.md:36-49` ("VM at 172.16.0.12 → 172.16.0.11:22 reachable") and the README admits it on `alcatraz.worker/README.md:40`. The fix (`br_netfilter` + `iptables -A FORWARD -i alcatraz0 -o alcatraz0 -j DROP`) is documented in the same file but not applied at startup. For a multi-tenant SaaS this is the load-bearing problem.
2. **Per-VM NFS exports are reachable cross-tenant.** `alcatraz.worker/internal/vm/agentfs/nfs_server.go:43-45` binds to the bridge gateway `172.16.0.1:port`, and the handler is `NullAuthHandler`. Any guest that can reach the right port mounts the corresponding overlay → cross-customer filesystem read.
3. **No sandbox quota.** Nothing in `Application/Sandboxes` or `Domain/Sandboxes` caps how many sandboxes a user can create. One paying user can exhaust slots and host disk.

### Silent data loss on the spawn path

4. **The outbox marks failed messages as processed.** `alcatraz.api/src/Alcatraz.Infrastructure/Outbox/ProcessOutboxMessagesJob.cs:51-61` catches the exception, logs it, then unconditionally calls `UpdateOutboxMessageAsync` which sets `processed_on_utc = now`. If NATS is briefly down when the spawn message is drained, the sandbox sits in `Provisioning` forever. `plans/end-to-end-wrap-up.md` flags this as a known gap; it's never been closed.
5. **A failed sshd probe still announces `vm.ready`.** `alcatraz.worker/internal/vm/spawn.go:199-200` — "sshd probe timed out, announcing vm.ready anyway". The API marks the sandbox `Running`, the cert is issued, the customer's first SSH times out. Failure should be a `Failed` state, not a fake-ok.
6. **`alcatraz.routes` loses state on restart.** Plain NATS subscribe, no JetStream durable consumer, no startup snapshot. Bounce routes and every active sandbox is unrouted until a fresh `vm.ready`/`vm.destroyed` arrives.

### Trust boundaries

7. **Keycloak admin-client secret is in `appsettings.Development.json` (line 40-43)** — committed to git. Postgres password is also literal `postgres`.
8. **NATS has no auth in compose**, and worker → api/routes messages are unsigned. Any process that can reach `:4222` can publish a `vm.ready` claiming sandbox X is at IP Y; the control plane and routes both believe it.
9. **CA private key is a plaintext file** (`/run/alcatraz-ca/alcatraz_ca`), no rotation runbook documented.

### Lifecycle and resource hardening

10. **No host-level resource caps on Firecracker.** Slot pool caps concurrency but no cgroup memory/CPU limits per VM.
11. **No timeouts on `m.Start`/`m.StopVMM`/`m.Wait`** — a wedged Firecracker hangs the spawn handler.
12. **No disk cap on AgentFS overlays.** A guest `dd if=/dev/zero` fills the host.
13. **`cni_sweep.go` only sweeps IPAM**, not orphaned `fc-tap*` or netns. Worker crash → manual cleanup before restart.

### Code hygiene

14. **Bookings + Apartments template code is still wired.** `Controllers/Apartments`, `Controllers/Bookings`, seed data, migrations. Dead surface area.
15. **No CI** — no GitHub Actions, no test gate on PRs.
16. **No metrics/tracing.** Logs only; "stuck spawn" has no alertable signal.

---

## Plan (three waves)

Each wave is independently shippable; W1 is the must-do-before-real-customers cut, W2 is private-beta-quality, W3 is customer polish.

### Wave 1 — Block the foot-guns (~1 week)

| # | Item | Touchpoint |
|---|---|---|
| W1.1 | Apply `br_netfilter` + `FORWARD -i alcatraz0 -o alcatraz0 -j DROP` at worker startup; assert with a smoke check | `cmd/alcatraz-worker/main.go` + new `internal/vm/isolation.go` |
| W1.2 | Restrict per-VM NFS reachability so VM B can't dial VM A's NFS port (per-spawn iptables rule keyed on src+dst port; medium-term: per-VM netns on the worker side) | `internal/vm/agentfs/nfs_server.go` + spawn flow |
| W1.3 | `MaxSandboxesPerUser` (config-driven, default 5) enforced in `CreateSandboxCommandHandler` + functional test | `Application/Sandboxes/CreateSandbox` |
| W1.4 | Stop marking failed outbox rows processed; add retry counter + max attempts; reaper flips sandbox to `Failed` after N persistent errors | `ProcessOutboxMessagesJob.cs` |
| W1.5 | sshd-probe timeout publishes `vm.failed` (or omits `vm.ready`) and the API marks sandbox `Failed` | `spawn.go:199`, new `Failed` transition |
| W1.6 | Move Keycloak/Postgres/CA secrets to env, rotate the values currently in git history | `docker-compose.yml`, `appsettings.Development.json`, README |
| W1.7 | Turn on NATS auth with per-component creds (`api`, `worker`, `routes`) and subject-scoped permissions | `docker-compose.yml` + each component's connect call |
| W1.8 | Delete Bookings/Apartments — controllers, application/domain slices, seeds, migrations | sweeping cleanup PR |

### Wave 2 — Reliability + observability (~1 week)

| # | Item |
|---|---|
| W2.1 | JetStream durable consumers for `vm.ready`/`vm.destroyed` so routes recovers state on restart (or alternative: `routes.requestSnapshot` reply pattern from API) |
| W2.2 | Timeouts on `m.Start`/`m.StopVMM`/`m.Wait`; force-kill on timeout; mark sandbox `Failed` |
| W2.3 | Wrap each Firecracker process in a transient systemd scope (`MemoryMax`, `CPUQuota`); cap overlay size, fail spawn if exceeded |
| W2.4 | Extend `cni_sweep.go` to reap orphan `fc-tap*` and netns at startup |
| W2.5 | Document the CA-key rotation runbook end-to-end; rehearse it once on staging |
| W2.6 | Persist cert issuances (`sandbox_id, owner_user_id, key_fingerprint, valid_until_utc, issued_at_utc, request_ip`) in a new audit table |
| W2.7 | Functional test for cross-user sandbox access (404 on every endpoint); integration test asserting VM-to-VM is blocked |
| W2.8 | `/health` gates on NATS + Postgres reachability |
| W2.9 | OpenTelemetry → Prom: spawn duration histogram, spawn-failure-reason counter, active-sandboxes gauge per user |
| W2.10 | GitHub Actions CI: build + `dotnet test` + `make test`; gate merges |

### Wave 3 — Customer polish (~3–5 days)

- W3.1 CLI error UX: map NATS-down / stuck-provisioning / expired-cert / unreachable-gateway to actionable messages.
- W3.2 `alcatraz sandbox list` shows provisioning age + reason.
- W3.3 Plumb `Failed` through API + CLI as a first-class state.
- W3.4 Sandbox-scoped correlation ID stamped at create, propagated through API/worker/routes logs.
- W3.5 Customer-facing quickstart, troubleshooting, "what persists / what doesn't" doc.

### Explicit non-goals for MVP (real concerns, but defer)

- **KRL / sub-TTL revocation** — 24h TTL is the revocation primitive; document it.
- **Multi-host worker pool / non-colliding subnets** — single-host is fine for first paying customer; address before scaling out.
- **HSM/KMS for the CA key** — file mount with tight FS perms is acceptable for first beta.
- **Billing/metering** — out of scope per design; W2.6 + a sandbox-lifecycle audit table give you the raw data when you wire it later.

---

## Recommended first PR

The highest-leverage single PR is **W1.1 + W1.2 + W1.3 + W1.8** — cross-VM L2 isolation, NFS-port isolation, sandbox quota, and the Bookings/Apartments deletion. Those four close the "another customer can hurt me" class entirely, which is the only class that genuinely makes Alcatraz unsafe to charge money for today. Everything else is hardening that can ship behind a private-beta flag.
