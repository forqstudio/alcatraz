# ADR-0005: NATS as the only coupling between API and worker

- **Status:** Accepted
- **Date:** 2026-05-10
- **Related:** [`0001-core-nats-over-jetstream.md`](0001-core-nats-over-jetstream.md), [`0003-transactional-outbox.md`](0003-transactional-outbox.md), [`../nats-broker.md`](../nats-broker.md)

## Context

`alcatraz.api` runs in containers — no KVM, no root, no host networking. `alcatraz.worker` runs on bare hosts — KVM, CNI bridge, root, direct access to Firecracker. They have incompatible deployment shapes and incompatible privilege envelopes; coupling them with anything synchronous (REST, gRPC) means:

- The API has to know each worker's address and health, turning the control plane into a service-discovery layer for the data plane.
- A worker outage propagates to the API as request-time failures.
- Scaling the control plane requires coordinated changes on the worker side, and vice versa.

Constraint 5 in the architecture overview is explicit about the desired property: **operator-friendly fleet — multi-host worker pool, control plane scales independently of host capacity.**

## Decision

The API and worker are **strangers**. They exchange only NATS messages with snake_case JSON payloads. There is no REST, no gRPC, no shared library, no service discovery between them. Four subjects carry the entire VM lifecycle:

| Subject        | Publisher    | Consumer queue group                                  | Payload                              | Purpose                              |
| -------------- | ------------ | ----------------------------------------------------- | ------------------------------------ | ------------------------------------ |
| `vm.spawn`     | API (outbox) | `worker-vm-spawn`                                     | `{id, vcpus, memory_mib, customer_id}` | Request VM spawn                     |
| `vm.destroy`   | API (outbox) | `worker-vm-destroy`                                   | `{id}`                               | Request VM teardown                  |
| `vm.ready`     | Worker       | `api-vm-ready`, plus fanout (no group) for `alcatraz.routes` | full boot metadata                   | VM is up and sshd responding         |
| `vm.destroyed` | Worker       | `api-vm-destroyed`, plus fanout for `alcatraz.routes` | `{id}`                               | VM exited (post-cleanup, slot release) |

Conventions:

- **Queue-group naming:** `<consumer>-<subject>`. `worker-vm-spawn`, `api-vm-ready`, etc.
- **Fanout = no queue group.** `alcatraz.routes` subscribes without a group so every replica builds the full registry.
- **Atomic publish via the outbox** ([ADR-0003](0003-transactional-outbox.md)) — `vm.spawn` is published exactly when a sandbox row exists.
- **Core NATS for lifecycle subjects, JetStream for billing subjects** ([ADR-0001](0001-core-nats-over-jetstream.md), [ADR-0012](0012-jetstream-for-billing-subjects.md)) — lifecycle uses the outbox for publish-side durability; billing has no outbox and so rides JetStream with explicit ack-after-DB-commit. Trade-offs and failure modes are documented in those ADRs.

The full broker reference (every producer, consumer, contract, and configuration knob) lives in [`../nats-broker.md`](../nats-broker.md).

Sandbox lifecycle states are: `Provisioning(1) → Running(2) → Deleting(3) → Deleted(4)` plus `Failed(5)`. `MarkRunning` requires `Provisioning`; `MarkDestroyed` is idempotent on terminal states. `vm.destroyed` on `Provisioning|Running` → `Failed` (unexpected exit); on `Deleting` → `Deleted`.

## Consequences

### Positive

- **Independent scale and lifecycle.** API replicas and worker replicas come and go without each other's knowledge. The broker absorbs the asynchrony.
- **No service discovery in the control plane.** The API does not know how many workers exist, where they live, or which one took its message.
- **Clean failure isolation.** A worker host kernel panic doesn't cascade into 5xx on the API; spawn requests queue at the broker until a worker is back.
- **Competing consumers for free.** Two workers connected to `worker-vm-spawn` automatically split the load; adding a third worker is a process start.
- **Fanout for read models.** `alcatraz.routes` replicas all converge on the same registry without coordinating with each other.

### Negative

- **At-most-once on consume** when combined with [ADR-0001](0001-core-nats-over-jetstream.md). A consumer crash mid-handler loses the message; recovery is manual today.
- **No request/reply.** "Did the worker actually accept this spawn?" is not directly answerable — the API knows only that the message was published. The state machine reconstructs that signal asynchronously through `vm.ready` / `vm.destroyed`.
- **Schemas are implicit.** The wire format is plain JSON with no envelope or version tag; subject-renames-as-versioning is a known limit (see ADR-0001's "When to revisit").
- **Operator UX depends on dashboarding NATS.** With no synchronous channel, monitoring queue depth, slow consumers, and unprocessed outbox rows is the only way to spot a stuck pipeline.
