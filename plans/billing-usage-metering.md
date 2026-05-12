# Usage Metering for Billing (v1, systemd + JetStream)

## Context

Alcatraz bills customers for sandbox usage. Payment provider is out of
scope; this plan covers everything up to a durable, queryable usage record
per sandbox.

A prior draft (`plans/billing-usage-metering.md`) used **manual cgroup
mkdir** for per-VM CPU isolation and **plain core NATS** for the new
metering subjects. This revision changes both:

- **Per-VM cgroup via `systemd-run --scope`**. The worker already runs as
  root on the host (not containerized — confirmed in
  `alcatraz.worker/README.md:176` and host invocation `sudo -E
  ./alcatraz.worker/bin/alcatraz-worker`), so systemd is directly
  reachable. Letting systemd own the transient unit means it also handles
  cleanup, and we get a predictable, hierarchical cgroup path with no
  manual file-twiddling.
- **JetStream for the new `vm.usage_sample` / `vm.usage_final` subjects**.
  NATS already launches with `-js` (`docker-compose.yml:98`) but no streams
  or persistence are configured today. Usage data is the first thing that
  *must not be silently dropped*, so it warrants at-least-once delivery
  with explicit acks. **Existing `vm.ready` / `vm.destroyed` stay on
  core NATS** — out of scope to migrate.

Dimensions, schema, and lifecycle semantics from the prior draft are
preserved. This document is self-contained; you do not need the earlier
plan to execute this one.

## Dimensions (unchanged)

| Dimension     | Mode        | Source                                       |
| ------------- | ----------- | -------------------------------------------- |
| vCPU          | provisioned | `Sandbox.ActualVcpus × window_seconds`       |
| RAM           | provisioned | `Sandbox.ActualMemoryMib × window_seconds`   |
| CPU time      | actual      | cgroup v2 `cpu.stat` (systemd scope)         |
| Network bytes | actual      | Firecracker `/metrics` (`net.rx_bytes_count`, `net.tx_bytes_count`) |

Disk and actual-RAM remain deferred for v1.

## Architecture

```
┌────────────────── alcatraz.worker (host root) ─────────────────────┐
│                                                                    │
│  spawn.go                                                          │
│    │                                                               │
│    ├── Firecracker Config.MetricsPath                              │
│    │      → /run/alcatraz/<vm_id>/metrics.fifo                     │
│    │                                                               │
│    ├── VMCommandBuilder.Build(ctx) → *exec.Cmd                     │
│    │     mutate cmd: wrap in `systemd-run --scope                  │
│    │       --unit=alcatraz-vm-<id>.scope                           │
│    │       --slice=alcatraz.slice -- <fc-bin> <fc-args...>`        │
│    │                                                               │
│    ├── m.Start() → scope unit created, fc child PID owned by scope │
│    │                                                               │
│    └── metering.Start(...)                                         │
│           cgroupPath := /sys/fs/cgroup/alcatraz.slice/             │
│                          alcatraz-vm-<id>.scope                    │
│           goroutine, 60s tick:                                     │
│             • read cgroup cpu.stat usage_usec                      │
│             • read last JSON object from metrics.fifo              │
│             • js.PublishAsync("vm.usage_sample", ...)              │
│           on m.Wait() return:                                      │
│             • js.PublishSync("vm.usage_final", ...) (await PubAck) │
│             • systemd reaps the scope → cgroup removed             │
│                                                                    │
└──────────────────────────────┬─────────────────────────────────────┘
                               │ JetStream
                               ▼
┌────────────────── alcatraz.api ────────────────────────────────────┐
│                                                                    │
│  JetStreamHostedService                                            │
│    • on startup: CreateOrUpdateStream(ALCATRAZ_USAGE, interest,    │
│         subjects=[vm.usage_sample, vm.usage_final], file storage)  │
│    • CreateOrUpdateConsumer(usage-sample, usage-final)             │
│                                                                    │
│  UsageSampleConsumer (durable pull consumer)                       │
│  UsageFinalConsumer  (durable pull consumer)                       │
│    • each: fetch → persist row → AckAsync()                        │
│    • DB commit happens BEFORE ack (at-least-once)                  │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
```

## Systemd-run wrapping — exact mechanics

### Why scope, not service

`systemd-run --scope` is the right choice for processes we want to launch
ourselves and have systemd track in a transient unit. (Compare `--service`,
which would have systemd *fork* the process — useless here because the
firecracker-go-sdk needs to own the `*exec.Cmd`.) `--scope` keeps our
parent → fc fork relationship intact: systemd-run forks, registers the
scope with systemd, and execs into the target. `cmd.Wait()` blocks on the
scope-leader process until exit, just like a non-wrapped invocation.

### Predictable cgroup path

```
/sys/fs/cgroup/<slice>/<unit>
=
/sys/fs/cgroup/alcatraz.slice/alcatraz-vm-<vm_id>.scope
```

We do **not** read `/proc/<pid>/cgroup` to discover the path — it's
deterministic from the unit name we chose. This saves a PID-discovery race
(systemd-run is a transient process whose PID is not the firecracker PID).

### Slice declaration

Create `/etc/systemd/system/alcatraz.slice` as part of worker deployment
(documented in `alcatraz.worker/README.md`; not in the worker itself, since
the worker is invoked manually today). The slice unit is minimal:

```ini
[Unit]
Description=Alcatraz Firecracker microVMs

[Slice]
# Future: place CPUAccounting=yes, MemoryAccounting=yes here if not default.
```

If the slice file is missing, `systemd-run --slice=alcatraz.slice` still
works — systemd creates a transient slice. The file just makes the slice
permanent and groupable for resource limits later.

### `spawn.go` wrap pattern

```go
// After firecracker.VMCommandBuilder...Build(ctx) returns *exec.Cmd:
fcCmd := firecracker.VMCommandBuilder{}.
    WithBin(spawnOptions.FirecrackerBin).
    WithSocketPath(instance.socket).
    Build(ctx)

unit := fmt.Sprintf("alcatraz-vm-%s.scope", instance.id)
wrapped := append(
    []string{"systemd-run",
        "--scope",
        "--collect",                       // auto-clean on exit
        "--quiet",
        "--unit=" + unit,
        "--slice=alcatraz.slice",
        "--property=Delegate=yes",         // we own the subtree
        "--",
    },
    fcCmd.Args...,                         // [fcBin, --api-sock, ..., --id, ...]
)
fcCmd.Path = "/usr/bin/systemd-run"
fcCmd.Args = wrapped
```

`--collect` ensures the unit is auto-cleaned when the scope leader exits,
even on non-zero exit. `--property=Delegate=yes` is mostly future-proofing
for nested controllers.

### Failure modes

| Scenario                                  | Behaviour                                                     |
| ----------------------------------------- | ------------------------------------------------------------- |
| `systemd-run` not on PATH                 | spawn fails fast; treated as a deployment misconfig — return error, no VM. |
| Unit name collision (stale scope)         | systemd refuses; spawn fails. Add a pre-spawn `systemctl reset-failed alcatraz-vm-*.scope` sweep at worker startup (one line in worker bootstrap; mirrors how `cni_sweep.go` already handles stale CNI state). |
| Scope created but firecracker crashes     | scope exits → `cmd.Wait()` returns → cleanup goroutine runs → metering.Stop() publishes final → systemd auto-cleans cgroup. |
| Worker process killed before fc exits     | systemd-run's parent died, but the scope persists because it's owned by systemd, not us. The fc process keeps running. Recovery on next worker boot is out of scope for v1 (same as today's stale-fc behaviour). |
| cpu.stat read from a freshly-created scope| empty/zero values until first scheduling slice. Acceptable. |

## JetStream design

### Stream

```
Name:        ALCATRAZ_USAGE
Subjects:    vm.usage_sample, vm.usage_final
Storage:     File
Retention:   Interest
Discard:     Old
Max msg size: 64 KiB  (per-message, ample for our payloads)
Replicas:    1  (single-node NATS)
```

Interest-based retention: a message is dropped once *every registered
consumer* has acked it. Both consumers ack-after-DB-commit, so an
in-flight API outage queues messages on disk until the API comes back
and drains them.

### Consumers

Two durable **pull** consumers, one per subject:

```
Stream:           ALCATRAZ_USAGE
Name:             usage-sample-consumer
Filter:           vm.usage_sample
AckPolicy:        Explicit
AckWait:          30s
MaxDeliver:       5
ReplayPolicy:     Instant
DeliverPolicy:    All
```

```
Stream:           ALCATRAZ_USAGE
Name:             usage-final-consumer
Filter:           vm.usage_final
AckPolicy:        Explicit
AckWait:          60s   (longer — final handler does more work)
MaxDeliver:       10
ReplayPolicy:     Instant
DeliverPolicy:    All
```

`MaxDeliver` caps redelivery to bound the impact of a poison-pill message.
After cap, the message lands in `$JS.EVENT.ADVISORY.CONSUMER.MAX_DELIVERIES`
— monitor that subject later (out of scope for v1; documented as a
follow-up).

### Stream/consumer provisioning

A new hosted service in `Alcatraz.Api` runs at startup and is idempotent:

```csharp
internal sealed class JetStreamProvisioningHostedService(
    NatsConnectionFactory connectionFactory,
    IOptions<NatsOptions> natsOptions,
    ILogger<JetStreamProvisioningHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var conn = await connectionFactory.GetConnectionAsync(ct);
        var js = new NatsJSContext(conn);

        await js.CreateOrUpdateStreamAsync(new StreamConfig
        {
            Name = "ALCATRAZ_USAGE",
            Subjects = new[] { "vm.usage_sample", "vm.usage_final" },
            Storage = StreamConfigStorage.File,
            Retention = StreamConfigRetention.Interest,
            Discard = StreamConfigDiscard.Old,
            MaxMsgSize = 64 * 1024,
            NumReplicas = 1,
        }, ct);

        await js.CreateOrUpdateConsumerAsync("ALCATRAZ_USAGE",
            new ConsumerConfig("usage-sample-consumer")
            {
                FilterSubject = "vm.usage_sample",
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                AckWait = TimeSpan.FromSeconds(30),
                MaxDeliver = 5,
            }, ct);

        await js.CreateOrUpdateConsumerAsync("ALCATRAZ_USAGE",
            new ConsumerConfig("usage-final-consumer")
            {
                FilterSubject = "vm.usage_final",
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                AckWait = TimeSpan.FromSeconds(60),
                MaxDeliver = 10,
            }, ct);

        logger.LogInformation("JetStream ALCATRAZ_USAGE stream + consumers ready");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

Register before the consumers in `DependencyInjection.cs` so the stream
exists by the time consumer background services start fetching.

### Producer (worker, Go)

`alcatraz.worker/internal/messaging/publisher.go` gets a JetStream context
on `NewPublisher`:

```go
js, err := jetstream.New(nc)
if err != nil { return nil, fmt.Errorf("jetstream: %w", err) }
```

New methods:

```go
func (p *Publisher) PublishUsageSample(ctx context.Context, payload VMUsageSamplePayload) error {
    data, _ := json.Marshal(payload)
    _, err := p.js.Publish(ctx, p.usageSampleSubject, data,
        jetstream.WithMsgID(p.sampleMsgID(payload)))  // dedup hint
    return err
}

func (p *Publisher) PublishUsageFinal(ctx context.Context, payload VMUsageFinalPayload) error {
    data, _ := json.Marshal(payload)
    _, err := p.js.Publish(ctx, p.usageFinalSubject, data,
        jetstream.WithMsgID(payload.SandboxID))       // 1 final per sandbox
    return err
}

func (p *Publisher) sampleMsgID(s VMUsageSamplePayload) string {
    return fmt.Sprintf("%s|%d", s.SandboxID, s.SampledAtUtc.UnixNano())
}
```

`Nats-Msg-Id` is JetStream's native server-side dedup: if the same MsgID
is re-published within the stream's dedup window (default 2 minutes,
configurable), the server drops the duplicate. This is **belt** —
suspenders is the DB unique index (see Schema below).

`PublishUsageFinal` is called **synchronously** in the cleanup goroutine
so we get a `PubAck` confirming durable storage before `vm.destroyed` is
published. `PublishUsageSample` is also synchronous (per-tick blocking is
fine at 60s cadence; simpler than async-handle bookkeeping).

### Consumer (API, .NET)

Replace the `IAsyncEnumerable<NatsMsg>` core-NATS pattern with a
JetStream pull loop:

```csharp
internal sealed class VmUsageSampleConsumer(
    NatsConnectionFactory connectionFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<VmUsageSampleConsumer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var conn = await connectionFactory.GetConnectionAsync(stoppingToken);
        var js = new NatsJSContext(conn);
        var consumer = await js.GetConsumerAsync(
            "ALCATRAZ_USAGE", "usage-sample-consumer", stoppingToken);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(
            cancellationToken: stoppingToken))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<UsageSamplePayload>(
                    msg.Data, JsonOptions);

                using var scope = scopeFactory.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();
                var result = await sender.Send(
                    new RecordSandboxUsageSampleCommand(payload), stoppingToken);

                if (result.IsFailure)
                {
                    logger.LogWarning("usage_sample handler failed: {Error}",
                        result.Error);
                    await msg.NakAsync(cancellationToken: stoppingToken);
                    continue;
                }

                await msg.AckAsync(cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "usage_sample: unhandled");
                await msg.NakAsync(cancellationToken: stoppingToken);
            }
        }
    }
}
```

Key shape rules:
- **DB commit before ack.** Handler scope's `DbContext.SaveChangesAsync()`
  must complete inside `sender.Send(...)` before `msg.AckAsync()`.
- **`NakAsync` on transient failures**, ack on success or poison. The
  consumer's `MaxDeliver` cap stops infinite retries.

`VmUsageFinalConsumer` mirrors this, dispatching
`MarkSandboxUsageRecordedCommand`.

## Schema (Alcatraz.Infrastructure migration)

Migration name: `Add_SandboxUsage`. Two new tables. No changes to
`sandboxes`.

### `sandbox_usage_samples`

| Column                       | Type                  | Notes |
| ---------------------------- | --------------------- | ----- |
| id                           | uuid PK               | |
| sandbox_id                   | uuid NOT NULL         | FK → sandboxes.id, ON DELETE CASCADE |
| sampled_at_utc               | timestamptz NOT NULL  | from worker |
| cpu_usage_usec_cumulative    | bigint NULL           | |
| net_rx_bytes_cumulative      | bigint NULL           | |
| net_tx_bytes_cumulative      | bigint NULL           | |

Indexes:
- `ix_sandbox_usage_samples_sandbox_id_sampled_at_utc` (sandbox_id, sampled_at_utc)
- **`ux_sandbox_usage_samples_sandbox_id_sampled_at_utc` UNIQUE** (sandbox_id, sampled_at_utc) — dedupe redelivered messages. The handler catches the unique-violation as "already recorded" and acks.

### `sandbox_usage_records`

One row per finalised sandbox. PK is `sandbox_id`.

| Column                          | Type                  | Notes |
| ------------------------------- | --------------------- | ----- |
| sandbox_id                      | uuid PK               | FK → sandboxes.id |
| billing_window_start_utc        | timestamptz NOT NULL  | = sandbox.ready_at_utc |
| billing_window_end_utc          | timestamptz NOT NULL  | = sandbox.deleted_on_utc, falling back to final.finalised_at_utc |
| provisioned_vcpu_seconds        | bigint NOT NULL       | |
| provisioned_memory_mib_seconds  | bigint NOT NULL       | |
| actual_cpu_usage_usec           | bigint NULL           | |
| actual_net_rx_bytes             | bigint NULL           | |
| actual_net_tx_bytes             | bigint NULL           | |
| sample_count                    | integer NOT NULL      | |
| finalised_at_utc                | timestamptz NOT NULL  | |

PK on `sandbox_id` gives natural idempotency for redelivered finals.

## Domain layer (Alcatraz.Domain)

Patterns mirror existing `Sandbox.cs` (`Alcatraz.Domain/Sandboxes/Sandbox.cs`):
private ctor + EF parameterless ctor, private setters, static factory
methods, raised domain events.

- `Alcatraz.Domain/Sandboxes/Usage/SandboxUsageRecord.cs`
  - `static SandboxUsageRecord Finalise(Sandbox sandbox, VmUsageFinal final, DateTime utcNow)`
    – computes provisioned, builds record, raises event.
  - PK = `sandbox.Id`.
- `Alcatraz.Domain/Sandboxes/Usage/SandboxUsageSample.cs` — anemic
  audit row.
- `Alcatraz.Domain/Sandboxes/Usage/ISandboxUsageRecordRepository.cs`
  - `GetAsync(Guid sandboxId, CancellationToken)`
  - `Add(SandboxUsageRecord)`
- `Alcatraz.Domain/Sandboxes/Usage/ISandboxUsageSampleRepository.cs`
  - `Add(SandboxUsageSample)`
- `Alcatraz.Domain/Sandboxes/Events/SandboxUsageRecordedDomainEvent.cs`
  – `record(Guid SandboxId) : IDomainEvent`. No handler in v1.
- `Alcatraz.Domain/Sandboxes/Usage/SandboxUsageErrors.cs`
  – `AlreadyRecorded`, `SandboxNotFinalisable`.

## Application layer (Alcatraz.Application)

- `Sandboxes/MarkSandboxUsageRecorded/`
  – `MarkSandboxUsageRecordedCommand` (carries `VmUsageFinal` DTO),
  handler, validator. Idempotent: existing record → success.
- `Sandboxes/RecordSandboxUsageSample/`
  – `RecordSandboxUsageSampleCommand` (carries `VmUsageSample` DTO),
  handler, validator. Catches `DbUpdateException` with unique-violation,
  returns success (duplicate redelivery).

## Infrastructure layer (Alcatraz.Infrastructure)

- `Configurations/SandboxUsageRecordConfiguration.cs` and
  `Configurations/SandboxUsageSampleConfiguration.cs` — mirror
  `SandboxConfiguration.cs` (snake_case columns, explicit types).
- `Repositories/SandboxUsageRecordRepository.cs` and
  `Repositories/SandboxUsageSampleRepository.cs` — inherit
  `Repository<T>` like existing repos.
- `Migrations/<timestamp>_Add_SandboxUsage.cs` — generated via
  `dotnet ef migrations add Add_SandboxUsage`.
- `Messaging/JetStreamProvisioningHostedService.cs` — stream/consumer
  provisioning (above).
- `Messaging/VmUsageSampleConsumer.cs` and
  `Messaging/VmUsageFinalConsumer.cs` — pull-consumer pattern (above).
- `Messaging/NatsOptions.cs` — add `UsageSampleSubject`,
  `UsageFinalSubject`, `UsageStreamName`, `UsageSampleConsumerName`,
  `UsageFinalConsumerName` with defaults.

Register the three new background services in
`DependencyInjection.cs` in this order:
`JetStreamProvisioningHostedService` →
`VmUsageSampleConsumer` → `VmUsageFinalConsumer`.

## Worker (alcatraz.worker)

### New package `alcatraz.worker/internal/metering/`

- `collector.go` — orchestrator. 60s ticker. Reads cgroup + FC metrics
  files. Publishes samples. On `Stop()`, publishes final synchronously
  awaiting PubAck.
- `firecracker.go` — `ReadLastMetrics(path string) (*FcMetrics, error)`.
  Tails file from end, parses last JSON object. Returns nil if empty.
- `cgroup.go` — `ReadCpuUsageUsec(cgroupPath string) (int64, error)`.
  Reads `cpu.stat`, parses `usage_usec <N>`.
- `*_test.go` — unit tests with fixture files for each parser; fake
  clock + stub publisher for collector.

No cgroup-creation function — systemd does that.

### `messaging/publisher.go`

- Add `js jetstream.JetStream` field.
- New `usageSampleSubject` and `usageFinalSubject` fields.
- New methods `PublishUsageSample` / `PublishUsageFinal` (above).
- `NewPublisher` signature gains the two subjects.

### `messaging/config.go`

Add to `Config`:
- `UsageSampleSubject` (default `vm.usage_sample`)
- `UsageFinalSubject` (default `vm.usage_final`)

Env overrides `NATS_USAGE_SAMPLE_SUBJECT`, `NATS_USAGE_FINAL_SUBJECT`.

### `vm/spawn.go`

Inside `Spawn`, after `agentfs.PrepareOverlay` and before
`firecracker.NewMachine`:

1. `runDir := filepath.Join("/run/alcatraz", instance.id)`;
   `os.MkdirAll(runDir, 0o755)`.
2. `metricsPath := filepath.Join(runDir, "metrics.fifo")`.
3. Add `MetricsPath: metricsPath,` to `firecracker.Config`.
4. After `VMCommandBuilder...Build(ctx)`, wrap `fcCmd` in `systemd-run`
   args (above).

After `m.Start()` succeeds and the sshd probe completes (just before the
existing `s.cleanupWG.Add(1)` block):

5. Construct `cgroupPath := "/sys/fs/cgroup/alcatraz.slice/alcatraz-vm-" + instance.id + ".scope"`.
6. `collector := metering.Start(ctx, metering.Options{...})`.
7. `instance.metering = collector`.

In the existing post-`m.Wait()` cleanup goroutine (`spawn.go:228`),
before `s.Release(index)`:

8. `if instance.metering != nil { instance.metering.Stop(context.Background()) }` —
   blocks until `vm.usage_final` PubAck'd.
9. `os.RemoveAll(runDir)`.

No explicit cgroup cleanup — systemd reaps the scope automatically.

### `vm/machine.go`

Add `metering *metering.Collector` field on `VirtualMachine`.

### Worker bootstrap (one-line addition)

In `cmd/alcatraz-worker/main.go`, after existing setup: shell out once to
`systemctl reset-failed 'alcatraz-vm-*.scope'` (best-effort; ignore
errors). Mirrors how `cni_sweep.go` clears stale CNI state.

## docker-compose.yml change

Add JetStream persistence to the `nats` service:

```yaml
nats:
  image: nats:2.10
  command: ["-js", "-sd", "/data/jetstream", "-m", "8222"]
  volumes:
    - nats_jetstream:/data/jetstream
  ports:
    - "4222:4222"
    - "8222:8222"
```

Plus a new named volume at the bottom:

```yaml
volumes:
  nats_jetstream:
```

## Configuration matrix

| Var                                 | Default                | Where  |
| ----------------------------------- | ---------------------- | ------ |
| `WORKER_METERING_INTERVAL`          | `60s`                  | worker |
| `WORKER_METERING_RUN_DIR`           | `/run/alcatraz`        | worker |
| `WORKER_METERING_CGROUP_ROOT`       | `/sys/fs/cgroup`       | worker |
| `WORKER_METERING_SYSTEMD_SLICE`     | `alcatraz.slice`       | worker |
| `NATS_USAGE_SAMPLE_SUBJECT`         | `vm.usage_sample`      | both   |
| `NATS_USAGE_FINAL_SUBJECT`          | `vm.usage_final`       | both   |
| `Nats__UsageStreamName`             | `ALCATRAZ_USAGE`       | API    |

## Failure modes & resilience

| Scenario                                   | Behaviour                                                              |
| ------------------------------------------ | ---------------------------------------------------------------------- |
| `systemd-run` missing                      | Spawn returns error; sandbox marked Failed. Deployment misconfig. |
| Stale scope from prior crash               | Bootstrap `reset-failed alcatraz-vm-*.scope` clears them. |
| API down when worker publishes             | JetStream persists message to `/data/jetstream`. Consumer drains on reconnect. |
| Worker crashes mid-sandbox                 | Samples up to last successful publish are in JetStream → API. No final → record never written for that sandbox (provisioned-only billing still derivable from `Sandbox` columns; cleanup sweep is out-of-scope follow-up). |
| Duplicate sample delivery                  | DB unique constraint `(sandbox_id, sampled_at_utc)` → handler treats as success and acks. |
| Duplicate final delivery                   | PK on `sandbox_id` → handler returns `AlreadyRecorded` as success and acks. |
| Poison message (corrupt JSON)              | `JsonException` → `NakAsync`. After `MaxDeliver` redeliveries, message lands in advisory subject (monitored as a v2 follow-up). |
| NATS server restart                        | Stream + messages survive (file storage); consumers resume from last ack. |

## Implementation order

A. **DB + domain skeleton**
   1. Domain entities, repos, errors, event
   2. EF configurations + Add_SandboxUsage migration
   3. DI wiring for repos

B. **JetStream infra**
   4. docker-compose.yml volume + `-sd` flag
   5. `NatsOptions` additions
   6. `JetStreamProvisioningHostedService`
   7. Verify stream/consumer creation via `nats stream ls` against the running container

C. **API consumers**
   8. `RecordSandboxUsageSampleCommand` + handler + validator
   9. `MarkSandboxUsageRecordedCommand` + handler + validator
   10. `VmUsageSampleConsumer`, `VmUsageFinalConsumer`
   11. Manual test: `nats pub vm.usage_sample '{...}'` and assert DB row + ack

D. **Worker metering**
   12. `metering/` package — parsers first (with tests), then collector (with tests)
   13. `messaging/publisher.go` + `config.go` JetStream additions
   14. `spawn.go` systemd-run wrap + metrics path + collector lifecycle
   15. `machine.go` field
   16. Worker bootstrap `reset-failed` sweep

E. **End-to-end smoke**
   17. `docker compose up`, run worker, spawn sandbox
   18. After 65s: `select * from sandbox_usage_samples where sandbox_id = ?` → ≥1 row
   19. Delete sandbox: `select * from sandbox_usage_records where sandbox_id = ?` → 1 row with provisioned + actuals
   20. Verify `systemctl status alcatraz-vm-<id>.scope` reports `inactive (dead)` post-cleanup

Each lettered phase is independently mergeable. B can land first to
flush out JetStream-config gotchas before any consumer logic depends
on it.

## Critical files to modify

**Worker (Go):**
- `alcatraz.worker/internal/vm/spawn.go` — systemd-run wrap, MetricsPath, collector hooks
- `alcatraz.worker/internal/vm/machine.go` — metering field
- `alcatraz.worker/internal/messaging/publisher.go` — JetStream context + new methods
- `alcatraz.worker/internal/messaging/config.go` — new subjects
- `alcatraz.worker/cmd/alcatraz-worker/main.go` — reset-failed sweep
- New: `alcatraz.worker/internal/metering/{collector,firecracker,cgroup}.go` + tests

**API (.NET):**
- `alcatraz.api/src/Alcatraz.Domain/Sandboxes/Usage/{SandboxUsageRecord,SandboxUsageSample,ISandboxUsageRecordRepository,ISandboxUsageSampleRepository,SandboxUsageErrors}.cs` — new
- `alcatraz.api/src/Alcatraz.Domain/Sandboxes/Events/SandboxUsageRecordedDomainEvent.cs` — new
- `alcatraz.api/src/Alcatraz.Application/Sandboxes/MarkSandboxUsageRecorded/{Command,Handler,Validator}.cs` — new
- `alcatraz.api/src/Alcatraz.Application/Sandboxes/RecordSandboxUsageSample/{Command,Handler,Validator}.cs` — new
- `alcatraz.api/src/Alcatraz.Infrastructure/Configurations/{SandboxUsageRecord,SandboxUsageSample}Configuration.cs` — new
- `alcatraz.api/src/Alcatraz.Infrastructure/Repositories/{SandboxUsageRecord,SandboxUsageSample}Repository.cs` — new
- `alcatraz.api/src/Alcatraz.Infrastructure/Migrations/<ts>_Add_SandboxUsage.cs` — generated
- `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/{JetStreamProvisioningHostedService,VmUsageSampleConsumer,VmUsageFinalConsumer}.cs` — new
- `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/NatsOptions.cs` — additions
- `alcatraz.api/src/Alcatraz.Infrastructure/DependencyInjection.cs` — register new services

**Infra:**
- `docker-compose.yml` — JetStream volume + `-sd` flag
- New: `deploy/systemd/alcatraz.slice` (documented in `alcatraz.worker/README.md`)

## Verification

End-to-end (after all phases):

```bash
# 1. Bring up the stack with persistent JetStream
docker compose up -d nats
nats stream ls               # expect ALCATRAZ_USAGE
nats consumer ls ALCATRAZ_USAGE   # expect usage-sample-consumer, usage-final-consumer

# 2. Bring up API (creates stream/consumers via hosted service)
docker compose up -d alcatraz.api

# 3. Start the worker on host
sudo -E ./alcatraz.worker/bin/alcatraz-worker

# 4. Spawn a sandbox via the API
curl -X POST .../api/v1/sandboxes -d '{"vcpus":4,"memoryMib":8192}'

# 5. Confirm the systemd scope exists
systemctl list-units 'alcatraz-vm-*.scope' --all

# 6. Wait 65s, confirm at least one sample row
psql -c "select count(*) from sandbox_usage_samples where sandbox_id = '<id>';"

# 7. Confirm cgroup cpu.stat is populating
cat /sys/fs/cgroup/alcatraz.slice/alcatraz-vm-<id>.scope/cpu.stat

# 8. Delete the sandbox via API
curl -X DELETE .../api/v1/sandboxes/<id>

# 9. Confirm final record + cleanup
psql -c "select * from sandbox_usage_records where sandbox_id = '<id>';"
systemctl list-units 'alcatraz-vm-<id>.scope' --all   # expect no rows
ls /run/alcatraz/<id>                                  # expect not found
```

Unit/integration:

```bash
# Worker
cd alcatraz.worker && go test ./internal/metering/...

# API
cd alcatraz.api && dotnet test
```

Targeted JetStream resilience smoke:

```bash
# Publish a sample with NATS down to verify reconnect/retry
docker compose stop nats
# (worker should buffer; observe slog warnings)
docker compose start nats
# Sample should land in DB within a few seconds.
```

## Out-of-scope follow-ups

- Disk billing (AgentFS quota or NFS byte counters)
- Sweep job synthesising final-from-samples after worker crashes
- Read API surface for usage (`alcatraz sandbox usage <id>`)
- Pricing / invoicing / aggregation
- Migrating `vm.ready` / `vm.destroyed` onto JetStream
- Monitoring `$JS.EVENT.ADVISORY.CONSUMER.MAX_DELIVERIES` for poison messages
- Multi-replica NATS (HA) once we leave single-node
