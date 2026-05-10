# Hibernation Strategies for Alcatraz Firecracker VMs

## Context

At today's defaults (4 vCPU, 8 GiB RAM committed per VM, hardcoded `MaxVMs = 5` per worker), a worker holds **40 GiB of RAM hostage 24/7** regardless of whether customers are actively using their sandboxes. Coding-agent sessions are mostly idle (waiting on a model, on the human, or on a long-running build), so committed memory is the dominant cost driver and the binding constraint on density. Hibernation — freezing idle VMs to disk and lazily resuming them on reconnect — is the single biggest density lever available, and the precondition for the unit economics to work.

The overlay subsystem is already hibernation-shaped: per-VM SQLite at `<AgentfsData>/<agent-id>.db` survives stop/start by design, keyed on `agent-id`, and `PrepareOverlay()` (`alcatraz.worker/internal/vm/agentfs/overlay.go:86-130`) is idempotent. The persistent-disk half is solved. This plan is about the memory half.

---

## Strategy landscape

Five realistic approaches, in rough order of complexity and density gain:

### A. Pause-only (Firecracker `PATCH /vm` state=Paused)
- vCPUs stop, **RAM stays committed**.
- Wake: ~1ms. Code change: a few lines.
- **Density gain: zero** — RAM is the bottleneck, not CPU. Skip.

### B. Snapshot + naive load
- `CreateSnapshot` → `StopVMM` → process exits → RAM freed. Resume = `LoadSnapshot` reads the full RAM file into a new process.
- Wake: 1–3s for 8 GiB.
- Simple but wastes I/O on every wake — even if the customer just pinged and walked away.

### C. Snapshot + UFFD (userfaultfd lazy page-in) ★ chosen for v1
- Same hibernate path as B. On resume, Firecracker is wired to a userfaultfd page-fault handler that mmaps the snapshot file and serves pages on demand.
- Wake: ~125ms (Firecracker reference figure). Untouched pages stay on disk → effective RAM stays low even after wake.
- Cost: a separate UFFD handler process per VM, careful socket lifecycle.

### D. Golden boot snapshot
- Boot a "golden" VM once, snapshot. All fresh VMs load from it instead of cold-booting.
- Drops *first-spawn* time from ~5–15s to ~250ms.
- Orthogonal to hibernation; synergistic. **v2.**

### E. Tiered idle policy
- e.g. 30s → pause, 5min → snapshot, 24h → tier to S3.
- Worth doing once C is in. **v2/v3.**

**Build order:** C (this plan) → D (cold-start) → object-storage tier for worker mobility → tiered policy.

---

## v1 implementation plan: snapshot + UFFD, local-only

### Architectural principles

- **No CLI invocation from application code.** Worker and API never shell out to `systemd-run`, `openssl`, `nft`, etc. Process supervision is the platform's job (systemd unit files written at host provisioning). Inter-component talk goes through Go libraries, syscalls, or RPC over Unix sockets/NATS. The existing `firecracker-go-sdk` process management stays — it's the SDK's contract, not our code shelling out.
- **Snapshot is a cache, not state of record.** The overlay DB is durable state. If a snapshot is lost (worker crash, Firecracker upgrade, deploy), we fall back to a fresh spawn against the same overlay. The customer sees "your files are here, processes restarted." This dissolves the "pinned to worker" anxiety — pinning is best-effort.

### Core architectural decisions

1. **UFFD pages are served by a long-running per-host broker daemon (`alcatraz-uffd-broker`)**, deployed and supervised by systemd at host provisioning time — *not* spawned by worker code. Worker talks to it over a Unix-domain gRPC socket: `RegisterHandler(snapshot_path, firecracker_uffd_socket_path) → handler_id`, `ReleaseHandler(handler_id)`. The broker opens userfaultfd via direct syscall, mmaps the snapshot, and services page faults from a goroutine pool with `runtime.LockOSThread()` to keep fault-handling threads off the GC's preemption list. One broker per host serves all VMs; isolation between VMs is per-handler not per-process.
   - **Why a broker and not in-process in the worker:** the worker's GC behavior is dominated by API/RPC traffic; pinning a low-latency page-fault path to that runtime is fragile. A dedicated broker process has predictable allocation patterns and can be tuned (`GOGC` setting, GOMEMLIMIT) independently.
   - **Why not spawn one UFFD process per VM:** that's exactly the systemd-run shell-out the constraint forbids. Multiplexing all VMs through one broker is cleaner *and* simpler.
2. **WorkerId on the Sandbox aggregate.** API needs to know which worker owns a hibernated VM to address resume requests. Workers publish a `worker.heartbeat` so dead workers can be detected and their hibernated VMs marked `HibernationLost`.
3. **Activity = connection-open count, not connection events.** Long-lived SSH sessions emit one TCP connect and zero further events at the routes layer. Track open connection count per sandbox; idle = zero open connections AND last-disconnect older than threshold.

### State model (API: `alcatraz.api/src/Alcatraz.Domain/Sandboxes/`)

Add to `SandboxStatus.cs`: `Hibernating`, `Hibernated`, `Resuming`, `HibernationLost`.

Add to `Sandbox.cs`:
- `WorkerId` (nullable string, set when `vm.ready` arrives)
- `LastActivityUtc` (UTC timestamp)
- `OpenConnectionCount` (int, maintained by routes events)
- `FirecrackerVersion` (string, captured at hibernate for skew detection)

### NATS subjects

| Subject | Direction | Payload |
|---|---|---|
| `vm.activity` | routes → API | `{id, opened: bool}` (open or close) |
| `worker.heartbeat` | worker → API | `{worker_id, ts}` (every 5s) |
| `worker.{workerId}.vm.hibernate` | API → worker | `{id}` |
| `vm.hibernated` | worker → API/routes | `{id, snap_path, fc_version}` |
| `worker.{workerId}.vm.resume` | API → worker | `{id}` |
| `vm.ready` | worker → routes (existing) | extend with `{worker_id}` |

Hibernate/resume requests flow through the **outbox** (consistent with spawn/destroy) — not direct publish.

### Worker hibernate flow (`alcatraz.worker/internal/vm/`)

New handler in `cmd/alcatraz-worker/main.go`, implementation in `internal/vm/hibernate.go`:

1. Receive `worker.{me}.vm.hibernate {id}`
2. Optional: trigger memory balloon to shrink resident RAM before snapshot (deferred to v1.1)
3. `machine.PauseVM()` (firecracker-go-sdk)
4. `machine.CreateSnapshot(memPath, statePath)` → writes `<AgentfsData>/<id>.snap.mem` + `<id>.snap.state` + `<id>.snap.fcversion` sidecar
5. `machine.StopVMM()`
6. **Keep the index pool slot allocated** — TAP/IP/NFS port must be reserved for resume to reproduce the exact network state captured in the snapshot
7. Publish `vm.hibernated`

### Worker resume flow

1. Receive `worker.{me}.vm.resume {id}`
2. Validate snapshot files exist and `*.snap.fcversion` matches running Firecracker. Mismatch → publish `vm.hibernation_lost`, API responds with fresh spawn.
3. **Order matters** (guest hangs on first I/O if NFS isn't ready):
   - gRPC `RegisterHandler` to broker: pass `snapshot_path` and the uffd socket path Firecracker will connect to (`/tmp/alcatraz-uffd-<id>.sock`)
   - Start NFS server on the *reserved* port (overlay DB still on disk)
   - `firecracker.NewMachine` configured with `mem_backend.backend_type=Uffd`, `backend_path=<uffd-sock>`, `snapshot_path=<state>`
   - `m.LoadSnapshot()` → `m.ResumeVM()`
4. Publish `vm.ready` (extended payload includes `worker_id`)
5. On `vm.destroy` for a resumed VM: gRPC `ReleaseHandler` to broker before stopping Firecracker.

### UFFD broker daemon

- Separate Go binary at `alcatraz.worker/cmd/alcatraz-uffd-broker/`. **Not invoked by worker code** — deployed as a systemd unit at host provisioning time (operational concern, lives in the deployment scripts under `alcatraz.worker/scripts/`).
- Listens on a Unix-domain gRPC socket (e.g. `/run/alcatraz-uffd-broker.sock`). Worker dials it as a gRPC client.
- API surface (small): `RegisterHandler(snapshot_mem_path, uffd_socket_path) → handler_id`, `ReleaseHandler(handler_id)`, `Stats() → {handlers, faults_served, ...}`.
- Implementation: per-handler goroutine that owns the uffd FD, locks its OS thread, mmaps the snapshot file, accepts Firecracker's connection on the uffd socket, services `UFFDIO_COPY` requests against mmap'd pages.
- Tuned independently via systemd unit env: `GOGC=200`, `GOMEMLIMIT=<host-aware>`, `GOMAXPROCS=<small>`.
- Failure mode: broker crash takes down all hibernated VMs on the host. Acceptable because snapshot-is-a-cache — they fall back to `HibernationLost` and respawn from overlay. Still, broker should restart on failure (systemd `Restart=always`) and worker should retry `RegisterHandler` once on transient gRPC errors.

### Wake-on-connect (`alcatraz.routes/`)

For v1, embed in the routes process (extract to separate service later if needed).

- On `vm.hibernated`, replace the Traefik route for `<sandbox-id>` to point at an embedded **wake-proxy** listener instead of the worker.
- Wake-proxy responsibilities:
  - Accept TCP connection, peek SNI (already happening at Traefik for routing — this listener does the same).
  - **Single-flight resume per sandbox-id** (mutex keyed on id) — concurrent connections during resume share one resume call.
  - Call API `POST /internal/sandboxes/{id}/resume`, which transitions to `Resuming` and outboxes `worker.{owner}.vm.resume`.
  - **Buffer all bytes received from the customer during resume.** Once `vm.ready` arrives, dial the worker's host:port, replay the buffer upstream, then bidirectionally pipe.
  - 10s wake-proxy timeout — stuck resume returns RST to the customer rather than piling up half-open sockets.
- On `vm.ready` for a resumed sandbox, restore the direct Traefik route.

### Idle scanner (API)

Quartz job `IdleSandboxScannerJob`, every 30s:
- Find `Status == Running AND OpenConnectionCount == 0 AND LastActivityUtc < now - threshold` (default 5 min, configurable).
- Transition to `Hibernating`, outbox `worker.{WorkerId}.vm.hibernate`.

Worker liveness scanner (separate Quartz job, every 15s):
- Find workers whose `worker.heartbeat` is stale (>30s).
- For all `Hibernated` sandboxes on that worker, transition to `HibernationLost`. Customer reconnect triggers a fresh spawn from the overlay.

### Cleanup

- `DeleteSandboxCommand` must remove `*.snap.{mem,state,fcversion}` (via `os.Remove`, not shell `rm`) *before* releasing the slot.
- On worker startup, scan `<AgentfsData>` for orphaned `*.snap.*` files whose sandbox is no longer in API → garbage collect.

### CLI nicety (`alcatraz.cli`)

When SSH fails with the "no mutual signature" error pattern (cert expired during long hibernation), auto-reissue cert and retry once. Cert TTL is 24h (`IssueSshCertificateCommandHandler.cs:21`); a hibernated VM resumed after that needs a fresh cert.

### Out of scope for v1 (deferred)

- Memory ballooning before snapshot (cuts snapshot size meaningfully — v1.1)
- Snapshot tiering to S3 / cross-worker mobility — v2/v3
- Tiered idle policy (pause-then-snapshot) — v2
- Golden boot snapshot for fast cold-start — v2
- Customer-controllable hibernation behavior — later
- Guest-agent activity heartbeat — only if connection-count signal proves insufficient

---

## Critical files to modify

**Worker (Go)**
- `alcatraz.worker/internal/vm/spawn.go` — extend with snapshot-aware spawn; ensure NFS bound before `LoadSnapshot`
- `alcatraz.worker/internal/vm/machine.go` — index pool: reserved-but-stopped state for hibernated VMs
- `alcatraz.worker/internal/vm/hibernate.go` — **new**: pause + snapshot + stop logic
- `alcatraz.worker/internal/vm/resume.go` — **new**: UFFD register + NFS rebind + load + resume
- `alcatraz.worker/cmd/alcatraz-worker/main.go` — new NATS handlers; heartbeat publisher
- `alcatraz.worker/cmd/alcatraz-uffd-broker/main.go` — **new**: long-running UFFD broker daemon (deployed via systemd at host provisioning, not spawned by worker)
- `alcatraz.worker/internal/uffd/client.go` — **new**: gRPC client used by worker to call the broker
- `alcatraz.worker/internal/firecracker/snapshot.go` — **new**: minimal HTTP-over-UDS client for `PUT /snapshot/load` with UFFD `mem_backend` field (works around SDK gap, see "SDK gap" section)
- `alcatraz.worker/scripts/alcatraz-uffd-broker.service` — **new**: systemd unit file for host-level provisioning
- `alcatraz.worker/internal/vm/agentfs/overlay.go` — already hibernation-friendly; no change expected

**API (C#)**
- `alcatraz.api/src/Alcatraz.Domain/Sandboxes/SandboxStatus.cs` — add states
- `alcatraz.api/src/Alcatraz.Domain/Sandboxes/Sandbox.cs` — add `WorkerId`, `LastActivityUtc`, `OpenConnectionCount`, `FirecrackerVersion`; transition methods
- `alcatraz.api/src/Alcatraz.Application/Sandboxes/Hibernate/HibernateSandboxCommand.cs` — **new**
- `alcatraz.api/src/Alcatraz.Application/Sandboxes/Resume/ResumeSandboxCommand.cs` — **new**
- `alcatraz.api/src/Alcatraz.Application/Jobs/IdleSandboxScannerJob.cs` — **new**
- `alcatraz.api/src/Alcatraz.Application/Jobs/WorkerLivenessJob.cs` — **new**
- Internal endpoint: `POST /internal/sandboxes/{id}/resume` for wake-proxy
- Reuse: existing `ProcessOutboxMessagesJob.cs` pattern, `IUserContext`, `IssueSshCertificateCommandHandler.cs`

**Routes (Go)**
- `alcatraz.routes/internal/registry/nats.go` — subscribe to `vm.hibernated`, `vm.ready` (existing) with worker_id, publish `vm.activity`
- `alcatraz.routes/internal/writer/traefik.go` — emit wake-proxy route for hibernated sandboxes
- `alcatraz.routes/internal/wakeproxy/` — **new**: SNI-aware buffered TCP proxy with single-flight resume

**CLI (C#)**
- `alcatraz.cli/src/Alcatraz.Cli/Commands/Ssh/SshLauncher.cs` — detect cert-expired error from openssh's stderr (the existing process-launch path is the CLI's purpose; no new shell-outs added), call API to re-issue cert, retry

---

## Verification

End-to-end:
1. Spawn a sandbox, SSH in, write a file (`echo hi > /tmp/foo`), disconnect.
2. Wait > idle threshold (configure to 30s for test). Confirm `Status == Hibernated` via API; confirm Firecracker process is gone (`pgrep firecracker` returns nothing for that id); confirm `<AgentfsData>/<id>.snap.{mem,state}` exist; confirm worker RAM dropped by ~`MemSizeMib`.
3. SSH back in. Measure handshake time (target <500ms beyond network RTT). Confirm `/tmp/foo` still present. Confirm `Status == Running`.
4. Kill the worker process. Confirm `WorkerLivenessJob` flips hibernated VMs to `HibernationLost`. Reconnect — confirm fresh spawn against the overlay (file still present, but `uptime` is fresh).
5. Concurrent resume: open 5 SSH sessions at once to a hibernated sandbox. Confirm only one `vm.resume` is published (single-flight). All 5 sessions land successfully.
6. Hibernate during active session: open SSH session, keep it open, wait past threshold. Confirm sandbox is NOT hibernated (open connection count > 0).
7. Firecracker version skew: hibernate, then bump worker's Firecracker binary version, restart. Confirm next resume produces `HibernationLost` and falls back cleanly.

Density check: spawn `MaxVMs` sandboxes, let all go idle. Confirm worker RSS drops to baseline + small per-VM overhead. Confirm `<AgentfsData>` disk usage ≈ `MaxVMs × MemSizeMib`.

## SDK gap (validated)

Firecracker server v1.15.1 (shipped in `alcatraz.core/bin/`) supports UFFD. But `firecracker-go-sdk v1.0.0` (the latest published release; changelog frozen ~2022) does not — its swagger-generated `SnapshotLoadParams` exposes only `MemFilePath`, `SnapshotPath`, `EnableDiffSnapshots`, `ResumeVM`. No `mem_backend` field; zero UFFD references anywhere in the SDK source.

Available SDK surface we *can* use:
- ✓ `Machine.PauseVM` (`machine.go:1105`)
- ✓ `Machine.ResumeVM` (`machine.go:1120`)
- ✓ `Machine.CreateSnapshot` (`machine.go:1135`)
- ✗ `Machine.loadSnapshot` is private and lacks UFFD config

**Workaround (chosen): bypass the SDK for the load call only.** Add a small `alcatraz.worker/internal/firecracker/snapshot.go` with an `http.Client` over `net.Dial("unix", m.Cfg.SocketPath)` that issues `PUT /snapshot/load` with our own JSON body including `mem_backend: { backend_type: "Uffd", backend_path: "<uffd-sock>" }`. ~80 lines. SDK still owns process spawn, machine config, networking, NFS handler, Pause/Resume/Create-snapshot. This is HTTP-to-Firecracker, not a CLI shell-out — consistent with the no-CLI-from-app-code principle.

Rejected alternatives: forking the SDK (maintenance burden), dropping UFFD for naive load (violates the <500ms wake target).

Risk that remains: future Firecracker upgrades may shift the request body shape. Mitigation: a single integration test that loads a known-good snapshot with UFFD against the pinned FC binary, run in CI on every dependency bump.
