# NATS Broker Reference

This document is the canonical reference for Alcatraz's NATS messaging surface: the broker topology, every producer and consumer, the message contracts on the wire, and the operational characteristics that follow from the current setup.

It complements rather than replaces:

- [`../README.md`](../README.md) — high-level lifecycle overview (sections "End-to-end request lifecycle" and "API ↔ Worker integration (NATS)").
- [`../plans/architecture.md`](../plans/architecture.md) — architecture summary including the NATS subjects table.
- [`../alcatraz.api/docs/outbox-pattern.md`](../alcatraz.api/docs/outbox-pattern.md) — full detail on the transactional outbox that produces `vm.spawn` / `vm.destroy`.
- [`adr/0001-core-nats-over-jetstream.md`](adr/0001-core-nats-over-jetstream.md) — why we run on core NATS and not JetStream, and what trade-offs that locks in.

## 1. Overview

Alcatraz uses **core NATS** (no JetStream consumers) as its async backbone between three services. Four subjects carry the entire VM lifecycle. Wire format is **plain JSON with `snake_case` field names**; there is no envelope, correlation header, or schema version embedded in the message — the subject *is* the type.

| Service           | Language    | Role in messaging                                      |
| ----------------- | ----------- | ------------------------------------------------------ |
| `alcatraz.api`    | .NET 8 (C#) | Publishes commands; consumes lifecycle events          |
| `alcatraz.worker` | Go          | Consumes commands; publishes lifecycle events          |
| `alcatraz.routes` | Go          | Consumes lifecycle events only (fanout, no queue group) |

The broker itself runs `nats:2.10` with `-js -m 8222` (`docker-compose.yml:95`). JetStream is enabled at the broker, but no service currently declares a stream or durable consumer — see §9.

## 2. Topology

```mermaid
flowchart LR
    subgraph API["alcatraz.api (.NET, 1 replica)"]
        api_pub["NatsSandboxEventPublisher"]
        api_ready["VmReadyConsumer"]
        api_destroyed["VmDestroyedConsumer"]
    end

    subgraph WKR["alcatraz.worker (Go, N replicas)"]
        wkr_pub["Publisher"]
        wkr_spawn["spawn handler"]
        wkr_destroy["destroy handler"]
    end

    subgraph ROUTES["alcatraz.routes (Go, N replicas)"]
        routes_consumer["NATSConsumer"]
    end

    subgraph SUBJECTS["NATS subjects"]
        s1(["vm.spawn"])
        s2(["vm.destroy"])
        s3(["vm.ready"])
        s4(["vm.destroyed"])
    end

    api_pub -->|publish| s1
    api_pub -->|publish| s2
    s1 -->|"queue: worker-vm-spawn"| wkr_spawn
    s2 -->|"queue: worker-vm-destroy"| wkr_destroy
    wkr_pub -->|publish| s3
    wkr_pub -->|publish| s4
    s3 -->|"queue: api-vm-ready"| api_ready
    s4 -->|"queue: api-vm-destroyed"| api_destroyed
    s3 -->|"fanout (no queue group)"| routes_consumer
    s4 -->|"fanout (no queue group)"| routes_consumer
```

Asymmetry to notice: command subjects (`vm.spawn`, `vm.destroy`) load-balance across worker replicas via queue groups. Lifecycle subjects (`vm.ready`, `vm.destroyed`) load-balance across API replicas via queue groups *but* fan out to every routes replica — each gateway needs the full registry.

## 3. Lifecycle sequence diagrams

### Spawn flow

```mermaid
sequenceDiagram
    actor U as User
    participant API as alcatraz.api
    participant N as NATS
    participant W as alcatraz.worker
    participant R as alcatraz.routes

    U->>API: POST /sandboxes
    API->>API: Sandbox.Request() → outbox row
    Note over API: Quartz job dispatches<br/>SandboxRequestedDomainHandler
    API->>N: publish vm.spawn (SpawnPayload)
    N->>W: deliver (queue: worker-vm-spawn)
    W->>W: vm.Spawn() — boot Firecracker, probe sshd
    W->>N: publish vm.ready (VMReadyInfo)
    par API state convergence
        N->>API: deliver (queue: api-vm-ready)
        API->>API: MarkSandboxRunningCommand
    and Routes registry update
        N->>R: deliver (fanout — every replica)
        R->>R: registry.Set() → debounce → Traefik write
    end
```

### Destroy flow

```mermaid
sequenceDiagram
    actor U as User
    participant API as alcatraz.api
    participant N as NATS
    participant W as alcatraz.worker
    participant R as alcatraz.routes

    U->>API: DELETE /sandboxes/{id}
    API->>API: Sandbox.RequestDeletion() → outbox row
    Note over API: SandboxDeletionRequestedDomainHandler
    API->>N: publish vm.destroy (DestroyPayload)
    N->>W: deliver (queue: worker-vm-destroy)
    W->>W: mgr.Destroy() — StopVMM
    Note over W: post-exit cleanup goroutine
    W->>N: publish vm.destroyed (vmDestroyedPayload)
    par API state convergence
        N->>API: deliver (queue: api-vm-destroyed)
        API->>API: MarkSandboxDestroyedCommand
    and Routes registry update
        N->>R: deliver (fanout — every replica)
        R->>R: registry.Delete() → debounce → Traefik write
    end
```

## 4. Producers

| Service           | Subject        | Source                                                                                          | Trigger                                                              | Payload type          |
| ----------------- | -------------- | ----------------------------------------------------------------------------------------------- | -------------------------------------------------------------------- | --------------------- |
| `alcatraz.api`    | `vm.spawn`     | `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/NatsSandboxEventPublisher.cs:22`            | Outbox handler for `SandboxRequestedDomainEvent`                     | `SpawnPayload`        |
| `alcatraz.api`    | `vm.destroy`   | `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/NatsSandboxEventPublisher.cs:40`            | Outbox handler for `SandboxDeletionRequestedDomainEvent`             | `DestroyPayload`      |
| `alcatraz.worker` | `vm.ready`     | `alcatraz.worker/internal/messaging/publisher.go:75`                                            | Post-boot in `vm.Spawn()` after sshd probe succeeds                  | `VMReadyInfo`         |
| `alcatraz.worker` | `vm.destroyed` | `alcatraz.worker/internal/messaging/publisher.go:86`                                            | Post-exit cleanup goroutine after Firecracker process terminates     | `vmDestroyedPayload`  |

Both Go publishes call `nc.Publish` followed by `nc.Flush` to push the message synchronously rather than relying on the client's async batching window.

## 5. Consumers

| Service           | Subject        | Queue group              | Handler                                                                          | Effect                                                       |
| ----------------- | -------------- | ------------------------ | -------------------------------------------------------------------------------- | ------------------------------------------------------------ |
| `alcatraz.worker` | `vm.spawn`     | `worker-vm-spawn`        | `alcatraz.worker/cmd/alcatraz-worker/main.go:91`                                 | Invokes `vm.Spawn()` — boots a Firecracker VM                |
| `alcatraz.worker` | `vm.destroy`   | `worker-vm-destroy`      | `alcatraz.worker/cmd/alcatraz-worker/main.go:117`                                | Invokes `mgr.Destroy()` — stops the Firecracker process      |
| `alcatraz.api`    | `vm.ready`     | `api-vm-ready`           | `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/VmReadyConsumer.cs:26`       | Dispatches `MarkSandboxRunningCommand` via MediatR           |
| `alcatraz.api`    | `vm.destroyed` | `api-vm-destroyed`       | `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/VmDestroyedConsumer.cs:26`   | Dispatches `MarkSandboxDestroyedCommand` via MediatR         |
| `alcatraz.routes` | `vm.ready`     | **none (fanout)**        | `alcatraz.routes/internal/registry/nats.go:71`                                   | `registry.Set()` → debounced Traefik dynamic-config write    |
| `alcatraz.routes` | `vm.destroyed` | **none (fanout)**        | `alcatraz.routes/internal/registry/nats.go:87`                                   | `registry.Delete()` → debounced Traefik dynamic-config write |

Worker subscriptions use `ChanQueueSubscribe` with a 64-message buffered channel (`alcatraz.worker/internal/messaging/subscriber.go:42`); if the handler stalls, the standard NATS slow-consumer behavior applies. API consumers use `await foreach` over `connection.SubscribeAsync<byte[]>` and process messages serially per replica.

Queue groups follow the convention **`<service>-<subject-with-dots-as-dashes>`**, declared in code at:

- `alcatraz.worker/internal/messaging/config.go:14` (`worker-vm-spawn`, `worker-vm-destroy`)
- `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/NatsOptions.cs:13` (`api-vm-ready`, `api-vm-destroyed`)

## 6. Message contracts

Serialization is `System.Text.Json` with `JsonNamingPolicy.SnakeCaseLower` on the C# side and Go struct tags on the worker side. There is **no envelope**; the body is the payload object directly.

### 6.1 `vm.spawn` — API → Worker

Producer record: `SpawnPayload` at `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/NatsSandboxEventPublisher.cs:59`. Consumed by an inline anonymous struct in `alcatraz.worker/cmd/alcatraz-worker/main.go:92` (deserialized into `vm.CreateVirtualMachineInput`).

| Field         | Type   | Meaning                                          |
| ------------- | ------ | ------------------------------------------------ |
| `id`          | string | Sandbox UUID (server-generated)                  |
| `vcpus`       | int    | Requested vCPUs                                  |
| `memory_mib`  | int    | Requested memory in MiB                          |
| `customer_id` | string | Owner user UUID                                  |

### 6.2 `vm.destroy` — API → Worker

Producer record: `DestroyPayload` at `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/NatsSandboxEventPublisher.cs:61`. Consumed by an inline anonymous struct at `alcatraz.worker/cmd/alcatraz-worker/main.go:118`.

| Field | Type   | Meaning           |
| ----- | ------ | ----------------- |
| `id`  | string | Sandbox UUID      |

### 6.3 `vm.ready` — Worker → API + Routes

Producer struct: `VMReadyInfo` at `alcatraz.worker/internal/messaging/publisher.go:41`. API-side mirror: `VmReadyPayload` at `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/VmReadyConsumer.cs:116`. Routes only reads three fields; see `alcatraz.routes/internal/registry/nats.go:38`.

**Group A — customer-facing**

| Field               | Type          | Meaning                                |
| ------------------- | ------------- | -------------------------------------- |
| `id`                | string        | Sandbox UUID                           |
| `host`              | string        | VM IP reachable from the gateway       |
| `port`              | int           | SSH port on the VM                     |
| `actual_vcpus`      | int           | vCPUs the VM was actually given        |
| `actual_memory_mib` | int           | Memory the VM was actually given       |
| `boot_duration_ms`  | int64         | Total boot latency                     |
| `ready_at_utc`      | RFC3339 UTC   | When sshd answered                     |

**Group B — ops/support** (optional fields use JSON `null` when unavailable)

| Field               | Type           | Meaning                              |
| ------------------- | -------------- | ------------------------------------ |
| `vmm_version`       | string \| null | Firecracker version                  |
| `vmm_state`         | string \| null | Firecracker VM state                  |
| `firecracker_pid`   | int \| null    | Firecracker process ID                |
| `socket_path`       | string         | Firecracker API socket path          |
| `tap_device`        | string         | Host tap interface                   |
| `mac_address`       | string         | VM MAC                               |
| `vm_ip`             | string         | VM IP (same value as `host`)         |
| `host_gateway_ip`   | string         | Bridge gateway IP on the host         |
| `nfs_port`          | int            | AgentFS NFS server port               |
| `worker_slot_index` | int            | Slot index inside the worker         |
| `rootfs_path`       | string         | Host path to the rootfs image         |
| `kernel_path`       | string         | Host path to the kernel image         |

**Group C — boot phase telemetry** (logged on the API side, not persisted)

| Field                    | Type  | Meaning                               |
| ------------------------ | ----- | ------------------------------------- |
| `phase_overlay_prep_ms`  | int64 | Time to prepare the overlay rootfs    |
| `phase_fc_boot_ms`       | int64 | Firecracker boot time                 |
| `phase_sshd_probe_ms`    | int64 | Time waiting for sshd to answer       |

### 6.4 `vm.destroyed` — Worker → API + Routes

Producer struct: `vmDestroyedPayload` at `alcatraz.worker/internal/messaging/publisher.go:71`. API mirror: `VmDestroyedPayload` at `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/VmDestroyedConsumer.cs:83`.

| Field | Type   | Meaning      |
| ----- | ------ | ------------ |
| `id`  | string | Sandbox UUID |

## 7. Outbox → NATS bridge

The API never publishes to NATS from the request thread. Domain events raised by aggregates land in the `outbox_messages` table inside the same transaction as the aggregate write; a Quartz job later replays them through MediatR, and only those handlers call `ISandboxEventPublisher`. See [`../alcatraz.api/docs/outbox-pattern.md`](../alcatraz.api/docs/outbox-pattern.md) for the full machinery.

```mermaid
sequenceDiagram
    participant H as Command handler
    participant Agg as Sandbox aggregate
    participant DB as PostgreSQL
    participant Q as Quartz outbox job
    participant M as MediatR
    participant Pub as NatsSandboxEventPublisher
    participant N as NATS

    H->>Agg: state transition
    Agg->>Agg: RaiseDomainEvent(SandboxRequestedDomainEvent)
    H->>DB: SaveChangesAsync<br/>(aggregate + outbox row, single tx)
    Note over DB: outbox_messages.processed_on_utc IS NULL
    Q->>DB: SELECT unprocessed
    Q->>M: Publish(domainEvent)
    M->>Pub: SandboxRequestedDomainHandler.Handle
    Pub->>N: publish vm.spawn
    Q->>DB: UPDATE processed_on_utc
```

The outbox guarantees **at-least-once publish** — if the process crashes between `Publish` and the row update, the next poll will republish. Consumers must therefore tolerate duplicates (the aggregate state machine handles this by ignoring transitions that don't apply to the current state).

Sandbox is the only aggregate that publishes to NATS today. Booking, User, and Review all raise domain events but none of them have NATS handlers wired in.

## 8. Configuration matrix

The C# side reads `Nats__*` environment variables (ASP.NET configuration's `__` → `:` mapping); defaults live in `alcatraz.api/src/Alcatraz.Infrastructure/Messaging/NatsOptions.cs`. The Go side reads `NATS_*` variables; defaults live in `alcatraz.worker/internal/messaging/config.go:14` and `alcatraz.routes` reads its subjects from env directly (no defaults — set in `docker-compose.yml:156`).

| Setting               | C# env (api)                | Go env (worker / routes)    | Default              |
| --------------------- | --------------------------- | --------------------------- | -------------------- |
| Broker URL            | `Nats__Url`                 | `NATS_URL`                  | `nats://localhost:4222` |
| Spawn subject         | `Nats__SpawnSubject`        | `NATS_SUBJECT` (worker)     | `vm.spawn`           |
| Destroy subject       | `Nats__DestroySubject`      | `NATS_DESTROY_SUBJECT` (worker) | `vm.destroy`     |
| Ready subject         | `Nats__ReadySubject`        | `NATS_READY_SUBJECT` (worker, routes) | `vm.ready`  |
| Destroyed subject     | `Nats__DestroyedSubject`    | `NATS_DESTROYED_SUBJECT` (worker, routes) | `vm.destroyed` |
| API ready queue       | `Nats__ReadyQueueGroup`     | —                           | `api-vm-ready`       |
| API destroyed queue   | `Nats__DestroyedQueueGroup` | —                           | `api-vm-destroyed`   |
| Worker spawn queue    | —                           | `NATS_QUEUE_GROUP`          | `worker-vm-spawn`    |
| Worker destroy queue  | —                           | `NATS_DESTROY_QUEUE_GROUP`  | `worker-vm-destroy`  |

The Compose stack wires this together at `docker-compose.yml:12` (API) and `docker-compose.yml:156` (routes); the worker runs on the host and picks up `.env` next to its binary.

## 9. Delivery semantics & failure modes

These are the operational facts that follow from "core NATS only, no JetStream consumers" — see [ADR-0001](adr/0001-core-nats-over-jetstream.md) for why this was chosen and when to revisit:

- **No durability.** A subscription that is not connected when a message is published will never see it. If no worker is connected when `vm.spawn` is published, the message is dropped at the broker — and the outbox row is already marked processed (it considers a successful `nc.Publish` as done), so there is no republish.
- **No ack / no redelivery.** Subscriptions are auto-acked on receipt; there is no JetStream, no `msg.Ack()`, no retry budget. A handler crash after receipt = the message is gone.
- **No reconciler.** The only background job in the API is `ProcessOutboxMessagesJob`; there is no reaper for sandboxes stuck in the transitional `Provisioning` or `Deleting` states (`alcatraz.api/src/Alcatraz.Domain/Sandboxes/SandboxStatus.cs`). A lost `vm.ready` or `vm.destroyed` therefore leaves the row stuck indefinitely — recovery today means an operator updating the database directly, since there is no exposed re-publish endpoint either.
- **No DLQ.** A handler that consistently throws will log per attempt but does not route the message anywhere recoverable. Today this is acceptable because both worker handlers are idempotent retries from a *new* request, not from broker-driven redelivery.
- **Queue group semantics.** Within a queue group exactly one subscriber receives each message. Without a queue group every subscriber receives every message.
  - `vm.spawn` / `vm.destroy`: queue group → competing consumers across worker replicas.
  - `vm.ready` / `vm.destroyed`: queue group on the API side (one replica reconciles state) **but no queue group on routes** so every gateway replica converges on the same registry.
- **At-least-once on the publish *side***. The transactional outbox guarantees republish on crash, so consumers must dedupe. Worker handlers dedupe naturally by ID; the Sandbox aggregate state machine ignores transitions that don't apply to the current state.
- **Slow consumer risk.** Worker subscriptions use a 64-message buffered channel. If `vm.Spawn` blocks long enough that 64 spawn requests pile up, NATS will drop messages and log a slow-consumer warning. Capacity planning currently relies on worker concurrency staying below the broker's queue depth.

## 10. Future evolution

Forward-looking, not current state. Listed here so the descriptive sections above don't grow speculation:

- **JetStream streams + durable consumers** for replay, ack-based redelivery, and surviving worker restarts. The broker already runs with `-js`, so this is a code change, not an infrastructure change. See [ADR-0001](adr/0001-core-nats-over-jetstream.md) for the migration path and the triggers that should prompt the switch.
- **DLQ subject** (e.g. `vm.dlq.<original>`) for poison messages, written by a wrapper handler after N failed deliveries.
- **Schema versioning.** Either subject-suffixed (`vm.ready.v2`) or an envelope with `schema_version` / `occurred_at` / `correlation_id`. Today the wire has none of these.
- **Subject hierarchy** if more bounded contexts start publishing — e.g. promoting `vm.*` to `sandbox.vm.*` and adding `booking.*`, `billing.*` siblings.
- **Distributed tracing propagation** via NATS message headers (`Nats-Trace-Id` etc.), so a single sandbox request can be traced end-to-end across API → worker → routes.
