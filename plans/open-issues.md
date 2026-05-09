# Open issues

Tracker for everything that's *not* in `architecture.md`. Severity tags:

- **[P0]** — blocks charging real customers. Foot-guns and tenant-isolation gaps.
- **[P1]** — needed before private beta. Reliability, observability, hygiene.
- **[P2]** — customer polish.
- **[deferred]** — known concerns, explicitly out of scope for MVP. Listed so they aren't lost.

## Tenant isolation

- **[P0] Cross-VM L2 traffic on `alcatraz0` is not blocked.** Documented in `alcatraz.worker/docs/network-isolation.md` ("VM at 172.16.0.12 → 172.16.0.11:22 reachable"). Fix: at worker startup, `modprobe br_netfilter`, `sysctl net.bridge.bridge-nf-call-iptables=1`, then `iptables -A FORWARD -i alcatraz0 -o alcatraz0 -j DROP`. Add startup smoke check.
- **[P0] Per-VM NFS exports reachable cross-tenant.** `alcatraz.worker/internal/vm/agentfs/nfs_server.go` binds the AgentFS NFS server to the bridge gateway IP with `NullAuthHandler`. Any guest that reaches the right port mounts the corresponding overlay. Short-term fix: per-spawn iptables rule keyed on src TAP + dst NFS port. Medium-term: per-VM netns on the worker side.
- **[P0] No sandbox quota per user.** Nothing in `Application/Sandboxes` or `Domain/Sandboxes` caps creation. One paying user can exhaust slots and host disk. Add `MaxSandboxesPerUser` (config-driven, default 5) enforced in `CreateSandboxCommandHandler`.
- **[P1] No per-sandbox outbound egress policy.** A compromised sandbox can act as a bot/exfil node. Add default-deny + allow-list iptables on the worker, keyed on TAP, hooked into spawn/destroy.

## Secrets & trust

- **[P0] Keycloak admin secret committed to git.** `appsettings.Development.json:40-43`. Postgres password is also literal `postgres`. Move to env, rotate values currently in history.
- **[P0] NATS has no auth.** Compose ships open `:4222`. Worker → API/routes messages are unsigned, so any process that reaches the broker can publish a forged `vm.ready` claiming sandbox X is at IP Y; the control plane and routes both believe it. Per-component creds (`api`, `worker`, `routes`) with subject-scoped permissions.
- **[P0] CA private key is a plaintext file** at `/run/alcatraz-ca/alcatraz_ca`. No rotation runbook documented. Tighten FS perms; document and rehearse the dual-CA window rotation.

## Reliability — silent failure modes

- **[P0] Outbox marks failed messages as processed.** `ProcessOutboxMessagesJob.cs:51-61` catches the exception, logs, then unconditionally sets `processed_on_utc = now`. If NATS is briefly down when a spawn message is drained, the sandbox sits in `Provisioning` forever. Add retry counter + max attempts; reaper flips sandbox to `Failed` after N persistent errors.
- **[P0] sshd-probe timeout still announces `vm.ready`.** `alcatraz.worker/internal/vm/spawn.go:199-200` ("sshd probe timed out, announcing vm.ready anyway"). API marks sandbox `Running`, cert is issued, customer's first SSH times out. Should publish `vm.failed` (or omit `vm.ready`) and the API marks sandbox `Failed`.
- **[P1] `alcatraz.routes` loses state on restart.** Plain NATS subscribe; no JetStream durable consumer, no startup snapshot. Bounce routes and every active sandbox is unrouted until the next `vm.ready`/`vm.destroyed`. Either JetStream durable consumers, or a `routes.requestSnapshot` reply pattern from the API.
- **[P1] No timeouts on `m.Start` / `m.StopVMM` / `m.Wait`.** A wedged Firecracker hangs the spawn handler. Add timeouts; force-kill on timeout; mark sandbox `Failed`.
- **[P1] `cni_sweep.go` only sweeps IPAM**, not orphaned `fc-tap*` or netns. Worker crash → manual cleanup before restart. Extend to reap orphan TAPs + netns at startup.

## Resource hardening

- **[P1] No host-level cgroup caps per Firecracker.** Slot pool caps concurrency but no memory/CPU limits per VM. Wrap each Firecracker process in a transient systemd scope (`MemoryMax`, `CPUQuota`).
- **[P1] No disk cap on AgentFS overlays.** A guest `dd if=/dev/zero` fills the host. Cap overlay size; fail spawn if exceeded.

## Code hygiene

- **[P1] Bookings/Apartments template code still wired.** `Controllers/Apartments`, `Controllers/Bookings`, `Domain/Apartments`, `Domain/Bookings`, `Domain/Reviews`, seed data, migrations. Dead surface area. Sweeping deletion PR.
- **[P1] No CI.** No GitHub Actions, no test gate on PRs. Add workflow: build + `dotnet test` + `make test`; gate merges.
- **[P1] Realm-export drift.** No CI check that the committed realm JSON matches what's actually in Keycloak after admin-UI changes.

## Observability

- **[P2] No metrics or tracing.** Logs only; "stuck spawn" has no alertable signal. OpenTelemetry → Prometheus: spawn duration histogram, spawn-failure-reason counter, active-sandboxes-per-user gauge.
- **[P2] `/health` doesn't gate on NATS or Postgres reachability.** Add dependency checks.
- **[P2] No cert issuance audit table.** Issuance is logged but not persisted. Schema: `(sandbox_id, owner_user_id, key_fingerprint, valid_until_utc, issued_at_utc, request_ip)`.
- **[P2] No sandbox-scoped correlation ID.** Stamp at create; propagate through API/worker/routes logs.

## CLI polish

- **[P2] `--json` flag on `sandbox ssh-cert` emits ANSI-coloured human output** instead of raw JSON. Workaround: strip with `sed`. Fix in the Spectre.Console renderer (formatter selection on `--json`).
- **[P2] CLI error UX.** Map NATS-down / stuck-provisioning / expired-cert / unreachable-gateway to actionable messages.
- **[P2] `alcatraz sandbox list` doesn't show provisioning age or failure reason.**
- **[P2] `Failed` is not first-class through API + CLI.** State exists in the domain but isn't surfaced consistently.
- **[P2] CLI distribution.** Signed binaries via Homebrew tap + `winget`; version pin in API responses so an out-of-date CLI surfaces an upgrade prompt.

## Tests

- **[P1] No functional test for cross-user sandbox access** (404 on every endpoint when accessing someone else's sandbox).
- **[P1] No integration test asserting VM-to-VM is blocked** (pairs with the `br_netfilter` fix above).

## Multi-host (deferred)

- **[deferred] Per-host VM subnet allocation.** `alcatraz.worker/cni/alcatraz-bridge.conflist` hardcodes `172.16.0.0/24` everywhere. Two worker hosts = colliding VM IPs, breaks the gateway's `(worker_host, vm_ip)` routing tuple. Carve per-host `/24` from `172.16.0.0/16` at worker startup.
- **[deferred] Multi-host worker pool.** Capacity-aware scheduling, anti-affinity (don't pack one customer on one host). Comes with the per-host subnet work, not before.
- **[deferred] Gateway↔worker private control/data plane.** Today Traefik shares the host namespace via `network_mode: host`. Multi-host needs an underlay (BGP / WireGuard mesh / VPC routing) so Traefik can reach `172.16.0.0/24` on each worker.

## Future hardening (deferred)

- **[deferred] KRL / sub-TTL revocation.** 24h TTL is the revocation primitive today. The `GET /v1/ssh/krl` endpoint and the gateway/VM polling timer are designed but not built.
- **[deferred] HSM/KMS for the CA key.** AWS KMS / Azure Key Vault HSM-backed signing, or Vault SSH secrets engine. Today the CA key is a file mount with FS perms — acceptable for first beta. The API's `ssh-keygen -s` shell-out becomes a KMS `Sign` call when this lands. Revisit before GA / first SOC2 conversation.
- **[deferred] Managed Postgres + secrets manager.** Self-hosted Postgres is fine for beta. Move the API DB to RDS / Azure Database for PostgreSQL Flexible Server and secrets to AWS Secrets Manager / Azure Key Vault before customer SLAs apply. The "secrets to env" P0 above is the bridge step that makes this a config swap, not a refactor.
- **[deferred] Wildcard cert via DNS-01 ACME.** Single-cert-with-default approach is fine for the demo; wildcard `*.ssh.alcatraz.io` would need DNS provider API access. Revisit when adding strict TLS hostname verification.
- **[deferred] Keycloak production hardening.** External Postgres for Keycloak, brute-force protection enabled, TOTP/WebAuthn on the admin realm.
- **[deferred] Traefik HA.** Single replica fine for the demo. With more replicas the no-queue-group fanout subscription on `alcatraz.routes` is correct; add a TCP load balancer in front.
- **[deferred] Rate limiting / DoS at the edge.** Defer to upstream LB or Cloudflare.
- **[deferred] Billing / metering.** Out of scope per design. The cert-issuance audit table (P2 above) plus a sandbox-lifecycle audit table give you the raw data when you wire it later.

## Known cosmetic

- A stale outbox row from a prior dev cycle contains an `IDomainEvent` payload missing the `$type` discriminator; the drainer logs `Could not create an instance of type Alcatraz.Domain.Abstractions.IDomainEvent` on every poll. Cosmetic; clear the row or migrate the column.
- Orphaned `veth` interfaces remain on `alcatraz0` from VMs spawned by pre-destroy-subscriber worker generations. Harmless; `sudo ip link delete vethXXXX` per interface, or restart the worker.
