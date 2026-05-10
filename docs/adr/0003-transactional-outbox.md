# ADR-0003: Transactional outbox for atomic domain events → NATS

- **Status:** Accepted
- **Date:** 2026-05-10
- **Related:** [`0001-core-nats-over-jetstream.md`](0001-core-nats-over-jetstream.md), [`0005-nats-as-api-worker-coupling.md`](0005-nats-as-api-worker-coupling.md), [`../nats-broker.md`](../nats-broker.md), [`../../alcatraz.api/docs/outbox-pattern.md`](../../alcatraz.api/docs/outbox-pattern.md)

## Context

The `vm.spawn` message must be published exactly when the sandbox row exists in PostgreSQL — never before, never without. The alternative — publishing from inside the request handler after `SaveChangesAsync` — has two correctness holes:

1. **Lost messages.** If NATS is down at publish time and the publish fails after the DB transaction commits, the row exists but no spawn was published. The sandbox sits in `Provisioning` forever.
2. **Double messages.** If the publish *succeeds* but the DB transaction then rolls back (deferred FK violation, constraint check, anything), a worker spawns a VM for a sandbox that doesn't exist.

Both holes are race conditions a load test would surface and a customer would feel. The fix has to make "row exists" and "message published" the *same fact*, not two facts coordinated at runtime.

## Decision

Use a **transactional outbox**. Domain events are written to an `outbox_messages` table inside the same EF transaction as the aggregate write; a Quartz job replays them through MediatR, and the MediatR handlers are the only code that calls into NATS.

Flow:

1. An aggregate raises a domain event (e.g. `Sandbox.Request` raises `SandboxRequestedDomainEvent`).
2. The EF `SaveChangesAsync` interceptor serialises every raised event into `outbox_messages` with `TypeNameHandling.All` (so polymorphic `IDomainEvent` round-trips), in the same transaction as `INSERT INTO sandboxes`.
3. `ProcessOutboxMessagesJob` (Quartz) polls `WHERE processed_on_utc IS NULL`, dispatches each via MediatR to its `*DomainEventHandler`, which calls into infrastructure (`ISandboxEventPublisher.PublishSpawnAsync` → NATS).
4. Successful dispatch sets `processed_on_utc`. Delivery is **at-least-once on the publish side**: handlers must be idempotent. The API's `vm.ready` consumer is idempotent on `Running`; the worker's spawn handler is keyed on sandbox UUID.

The full mechanism — serialisation format, polling cadence, retry/error handling — is documented in [`../../alcatraz.api/docs/outbox-pattern.md`](../../alcatraz.api/docs/outbox-pattern.md).

## Consequences

### Positive

- **"Row exists ⇔ message published" is a database invariant**, not a runtime hope. Either both happen or neither does.
- **NATS downtime is non-blocking.** A spawn request returns 201 even if NATS is unreachable; the message drains when NATS recovers.
- **Domain handlers stay synchronous and pure.** They raise events; they never await infrastructure.
- **Same machinery handles other aggregates.** `Booking`, `User`, `Review` events all flow through the same outbox without per-aggregate plumbing.

### Negative

- **Latency shift.** `vm.spawn` is published ~1s after the API responds 201, not synchronously. Acceptable because the CLI polls until `Running` regardless.
- **At-least-once on publish, at-most-once on consume.** Combined with [ADR-0001](0001-core-nats-over-jetstream.md) (core NATS, no consumer-side redelivery), the durability guarantee is asymmetric. Operators triaging stuck sandboxes must hold both halves in their head.
- **Coarse failure handling today.** A failed dispatch still marks the row processed (see `plans/open-issues.md` — `[P0]` outbox swallows failures). Fix is a retry counter + reaper, **not** a redesign of the outbox.
- **Polling overhead.** Quartz polls on a fixed cadence rather than reacting to inserts. Fine at current scale; trigger-based notification is a future option if event volume climbs.

### When to revisit

- If a third bounded context starts publishing events at high volume and the polling cadence becomes the bottleneck, switch to `LISTEN/NOTIFY` from PostgreSQL.
- If the swallow-on-failure bug starts producing real incidents, ship the retry counter + reaper before considering anything more invasive.
