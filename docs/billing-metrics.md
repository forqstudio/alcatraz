# Billing Metrics

Alcatraz records per-sandbox usage so customers can be billed for the
resources their sandbox actually consumed. Payment integration is out of
scope for this repository; this document covers everything up to and
including the durable per-sandbox usage record the billing pipeline reads.

It complements rather than replaces:

- [`../plans/billing-usage-metering.md`](../plans/billing-usage-metering.md)
  — design doc with the systemd-run + JetStream rationale and the
  worker-side implementation walkthrough.
- [`nats-broker.md`](nats-broker.md) — the broader NATS surface. The
  billing subjects below are the only JetStream consumers in the system;
  the rest is core NATS.
- [`adr/0001-core-nats-over-jetstream.md`](adr/0001-core-nats-over-jetstream.md)
  — original "core NATS, no JetStream" ADR. Billing is the deliberate
  carve-out: usage messages must not be silently dropped, so they ride
  JetStream while `vm.ready` / `vm.destroyed` stay on core.

## 1. What gets measured

Two modes per dimension:

- **Provisioned** — what the sandbox was allowed to use, integrated over
  the billing window. Derived from existing `Sandbox` columns; nothing
  new to sample.
- **Actual** — what the sandbox really consumed, sampled from the host.
  Cumulative counters since VM boot.

| Dimension     | Mode        | Source                                                              |
| ------------- | ----------- | ------------------------------------------------------------------- |
| vCPU          | provisioned | `Sandbox.ActualVcpus × window_seconds`                              |
| RAM           | provisioned | `Sandbox.ActualMemoryMib × window_seconds`                          |
| CPU time      | actual      | cgroup v2 `cpu.stat` `usage_usec` (host-side, per systemd scope)    |
| Network bytes | actual      | Firecracker `/metrics` (`net.rx_bytes_count`, `net.tx_bytes_count`) |

The **billing window** is `ReadyAtUtc → DeletedOnUtc` (or `FinalisedAtUtc`
if the sandbox is still running when queried). A sandbox that never
reaches Running has a zero-length window and zero provisioned totals.

**Out of v1:** disk (storage + I/O), actual RAM (RSS). Rootfs is served
over NFS so there is no virtio-block device for Firecracker to report;
adding disk billing requires either an AgentFS quota or NFS-layer byte
counters, both follow-up work.

## 2. Customer surface

The CLI is the only customer-facing surface today. Two forms:

```bash
alcatraz usage              # list finalised usage records, newest first
alcatraz usage <sandbox-id> # one sandbox, live view while running, final view once exited
```

Both respect ownership: a customer can only see their own sandboxes.

`alcatraz usage <id>` against a running sandbox renders an **in-progress**
panel whose window end is `now`; CPU + network counters come from the
latest sample (60 s cadence, so values appear ~60 s after boot). The
same command against an exited sandbox shows the **finalised** record
with the final totals — that record never mutates after it's written.

`--json` on either form emits the raw response for piping.

## 3. Pipeline

```
┌────── alcatraz.worker (host root) ──────┐         ┌────── alcatraz.api ──────┐
│                                         │         │                          │
│  Spawn:                                 │         │ JetStreamProvisioning    │
│  - Firecracker MetricsPath →            │         │ HostedService (startup)  │
│    /run/alcatraz/<id>/metrics.fifo      │         │ - declares stream +      │
│  - VMCommandBuilder wrapped in          │         │   two pull consumers     │
│    systemd-run --scope                  │         │                          │
│    --slice=alcatraz.slice               │         │ VmUsageSampleConsumer    │
│    --unit=alcatraz-vm-<id>.scope        │         │ VmUsageFinalConsumer     │
│                                         │         │ - fetch → DB commit →    │
│  metering.Collector (60 s ticker)       │         │   AckAsync               │
│  - read /sys/fs/cgroup/alcatraz.slice/  │ NATS    │                          │
│    alcatraz-vm-<id>.scope/cpu.stat      │ Jet-    │ Tables                   │
│  - read last JSON object from           │ Stream  │  sandbox_usage_samples   │
│    metrics.fifo                         │────────▶│  sandbox_usage_records   │
│  - js.Publish("vm.usage_sample", …)     │         │                          │
│                                         │         │ GetSandboxUsageQuery     │
│  on Firecracker exit:                   │         │ ListSandboxUsageQuery    │
│  - js.Publish("vm.usage_final", …)      │         │ (HTTP under              │
│  - systemd reaps the scope              │         │  /api/v1/sandboxes/…)    │
│                                         │         │                          │
└─────────────────────────────────────────┘         └──────────────────────────┘
```

### 3.1 Per-VM cgroup via systemd-run

Each Firecracker process runs inside a transient systemd scope —
`alcatraz-vm-<sandbox-uuid>.scope` under `alcatraz.slice` — so its
cgroup is isolated, the path is deterministic, and systemd handles
cleanup. The worker mutates the `*exec.Cmd` built by the
firecracker-go-sdk's `VMCommandBuilder` to prepend
`systemd-run --scope --collect --quiet --unit=… --slice=… --
…`. Predictable cgroup path = no PID-discovery race, and `--collect`
removes the unit even on non-zero exit.

The worker's bootstrap (`alcatraz.worker/cmd/alcatraz-worker/main.go`)
runs `systemctl reset-failed alcatraz-vm-*.scope` at startup so stale
units from a previous crash don't block the next spawn from claiming
the same unit name.

### 3.2 Firecracker `/metrics`

The SDK accepts a `MetricsPath` in its `Config`. Firecracker opens the
file once and appends one JSON object per flush (default every 60 s)
plus one final flush at clean shutdown. The collector reads the last
complete JSON line each tick — trailing partial writes fall back to
the previous complete line.

Only `net.rx_bytes_count` / `net.tx_bytes_count` are read. `block.*`
counters are not used because rootfs is served over NFS, not virtio-blk
(see [Out of v1](#1-what-gets-measured)).

### 3.3 JetStream

A single stream, declared idempotently at API startup:

```
Name:       ALCATRAZ_USAGE
Subjects:   vm.usage_sample, vm.usage_final
Storage:    File (durable, /data/jetstream in compose)
Retention:  Interest — messages drop after every consumer acks
Max msg:    64 KiB
```

Two pull consumers:

| Consumer                | Filter            | AckWait | MaxDeliver |
| ----------------------- | ----------------- | ------- | ---------- |
| `usage-sample-consumer` | `vm.usage_sample` | 30 s    | 5          |
| `usage-final-consumer`  | `vm.usage_final`  | 60 s    | 10         |

Both ack policies are **explicit, after the DB transaction commits**.
That gives at-least-once delivery: a crash mid-handler results in
redelivery, never a silently dropped record.

Each `Publish` carries a `Nats-Msg-Id` header for server-side dedup
within the stream's 2-minute window:

- samples: `"{sandbox_id}|{sampled_at_utc.UnixNano()}"`
- final:   `sandbox_id` (one per sandbox)

The DB has matching idempotency guards (see
[Schema](#4-schema)) so a duplicate that escapes the dedup window
still lands at most once on disk.

### 3.4 Ordering guarantee

`vm.usage_final` is published from the worker's post-`m.Wait()` cleanup
goroutine **before** `vm.destroyed`. Both are awaited synchronously,
final via JetStream PubAck and destroyed via core NATS flush. The API
can therefore process `vm.destroyed` knowing that, if a usage record is
coming, it's already durably stored.

## 4. Schema

Two new tables in the API's PostgreSQL database. No changes to
`sandboxes`.

### `sandbox_usage_samples`

One row per 60 s tick during a sandbox's life. Append-only, audit-grade.

| Column                       | Type           | Notes                                |
| ---------------------------- | -------------- | ------------------------------------ |
| `id`                         | uuid PK        |                                      |
| `sandbox_id`                 | uuid NOT NULL  | FK → sandboxes.id, ON DELETE CASCADE |
| `sampled_at_utc`             | timestamptz    | from the worker collector            |
| `cpu_usage_usec_cumulative`  | bigint NULL    | cgroup `cpu.stat usage_usec`         |
| `net_rx_bytes_cumulative`    | bigint NULL    | Firecracker `net.rx_bytes_count`     |
| `net_tx_bytes_cumulative`    | bigint NULL    | Firecracker `net.tx_bytes_count`     |

Unique index on `(sandbox_id, sampled_at_utc)` — dedupes redelivered
JetStream messages. The handler detects "already exists" via an
`AnyAsync` check before insert; JetStream redelivery is sequential per
pull consumer, so check-then-insert is race-free.

### `sandbox_usage_records`

One row per finalised sandbox. PK is `sandbox_id`, giving natural
idempotency for redelivered finals.

| Column                            | Type           | Notes                                                   |
| --------------------------------- | -------------- | ------------------------------------------------------- |
| `id` (= sandbox_id)               | uuid PK        | FK → sandboxes.id, ON DELETE CASCADE                    |
| `billing_window_start_utc`        | timestamptz    | = `Sandbox.ReadyAtUtc`                                  |
| `billing_window_end_utc`          | timestamptz    | = `Sandbox.DeletedOnUtc`, fallback `FinalisedAtUtc`     |
| `provisioned_vcpu_seconds`        | bigint         | `ActualVcpus × window_seconds`                          |
| `provisioned_memory_mib_seconds`  | bigint         | `ActualMemoryMib × window_seconds`                      |
| `actual_cpu_usage_usec`           | bigint NULL    | final cumulative CPU time                               |
| `actual_net_rx_bytes`             | bigint NULL    | final cumulative rx                                     |
| `actual_net_tx_bytes`             | bigint NULL    | final cumulative tx                                     |
| `sample_count`                    | int            | number of intermediate samples (audit cross-check)      |
| `finalised_at_utc`                | timestamptz    | when the record was written                             |

Migration: `Alcatraz.Infrastructure/Migrations/<ts>_Add_SandboxUsage`.

## 5. HTTP API

All routes require a valid Keycloak JWT and the `Sandboxes.Read`
permission. All responses are owner-scoped — querying someone else's
sandbox returns 404.

| Method | Path                                  | Returns                              |
| ------ | ------------------------------------- | ------------------------------------ |
| `GET`  | `/api/v1/sandboxes/usage`             | `SandboxUsageResponse[]` (finalised) |
| `GET`  | `/api/v1/sandboxes/{id}/usage`        | `SandboxUsageResponse` (live or final) |

`SandboxUsageResponse` payload:

```jsonc
{
  "sandboxId":            "uuid",
  "ownerUserId":          "uuid",
  "finalised":            true,                  // false for live view
  "billingWindowStartUtc": "2026-05-12T05:57:59Z",
  "billingWindowEndUtc":   "2026-05-12T06:02:59Z", // = now for live view
  "provisionedVcpuSeconds": 600,
  "provisionedMemoryMibSeconds": 1228800,
  "actualCpuUsageUsec":     7300000,             // null if cgroup unreadable
  "actualNetRxBytes":       0,
  "actualNetTxBytes":       0,
  "sampleCount":            5,
  "finalisedAtUtc":         "2026-05-12T06:02:59Z" // null for live view
}
```

The list endpoint returns finalised records only, ordered by
`billing_window_end_utc` DESC. The single-sandbox endpoint computes a
**live view** when no finalised record exists: provisioned totals are
recomputed against the current clock, actuals come from the latest row
in `sandbox_usage_samples`.

## 6. Failure modes

| Scenario                              | Behaviour                                                                 |
| ------------------------------------- | ------------------------------------------------------------------------- |
| `systemd-run` missing on the host     | Spawn fails fast at the worker; sandbox is marked Failed. Deployment misconfig. |
| Stale `alcatraz-vm-*.scope` from prior crash | Cleared by `reset-failed` sweep on worker startup.                  |
| API down when worker publishes        | JetStream persists messages to `/data/jetstream`; consumers drain on reconnect. |
| Worker crashes mid-sandbox            | Intermediate samples up to the last successful publish are in JetStream → DB. No final record. Provisioned totals are still derivable from `Sandbox.ReadyAtUtc/DeletedOnUtc`; a sweep job to synthesise finals from samples is a follow-up. |
| cgroup unreadable                     | CPU fields are null in the sample / final; other dimensions unaffected.   |
| Firecracker `/metrics` file missing   | Network fields are null; other dimensions unaffected. Common in the first 60 s before the first flush. |
| Duplicate sample delivery             | Dedup at JetStream (MsgID), again at DB (unique index). Handler acks.     |
| Duplicate final delivery              | Dedup at JetStream (MsgID), again at DB (PK on `sandbox_id`). Handler acks. |
| Poison message (corrupt JSON)         | Handler `Nak`s; after `MaxDeliver`, lands in `$JS.EVENT.ADVISORY.CONSUMER.MAX_DELIVERIES`. Monitoring that subject is a follow-up. |

## 7. Configuration

| Variable                            | Default                | Where  |
| ----------------------------------- | ---------------------- | ------ |
| `WORKER_METERING_INTERVAL`          | `60s`                  | worker |
| `WORKER_METERING_RUN_DIR`           | `/run/alcatraz`        | worker |
| `WORKER_METERING_CGROUP_ROOT`       | `/sys/fs/cgroup`       | worker |
| `WORKER_METERING_SYSTEMD_SLICE`     | `alcatraz.slice`       | worker |
| `NATS_USAGE_SAMPLE_SUBJECT`         | `vm.usage_sample`      | both   |
| `NATS_USAGE_FINAL_SUBJECT`          | `vm.usage_final`       | both   |
| `Nats__UsageStreamName`             | `ALCATRAZ_USAGE`       | API    |
| `Nats__UsageSampleConsumerName`     | `usage-sample-consumer`| API    |
| `Nats__UsageFinalConsumerName`      | `usage-final-consumer` | API    |

NATS in `docker-compose.yml` mounts `nats_jetstream:/data/jetstream` and
launches with `-sd /data/jetstream` so JetStream state survives
`docker compose down`. Production deployments must replace this with
their own persistent volume.

## 8. Out-of-scope follow-ups

- Disk billing (AgentFS quota or NFS-layer counters).
- Sweep job synthesising finals from samples when the worker crashes.
- Pricing, invoicing, multi-tenant aggregation.
- Monitoring `$JS.EVENT.ADVISORY.CONSUMER.MAX_DELIVERIES` for poison messages.
- Multi-replica JetStream once we leave single-node NATS.
- Migrating `vm.ready` / `vm.destroyed` onto JetStream.
