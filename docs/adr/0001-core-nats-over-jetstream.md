# ADR-0001: Core NATS (no JetStream consumers) for inter-service messaging

- **Status:** Accepted (lifecycle subjects only; partially carved out by [ADR-0012](0012-jetstream-for-billing-subjects.md) for the billing subjects)
- **Date:** 2026-05-10
- **Deciders:** Alcatraz core team
- **Related:** [`0012-jetstream-for-billing-subjects.md`](0012-jetstream-for-billing-subjects.md), [`../nats-broker.md`](../nats-broker.md), [`../../plans/architecture.md`](../../plans/architecture.md) (§ "API ↔ Worker integration (NATS)"), [`../../alcatraz.api/docs/outbox-pattern.md`](../../alcatraz.api/docs/outbox-pattern.md)

## Context

Alcatraz uses NATS as the only async coupling between `alcatraz.api`, `alcatraz.worker`, and `alcatraz.routes` (see [the broker reference](../nats-broker.md) for the full topology). NATS supports two delivery models that meaningfully differ for this use case:

- **Core NATS** — fire-and-forget pub/sub, auto-ack on receipt, no broker-side persistence, no redelivery. If no subscriber is connected at publish time, the message is dropped at the broker.
- **JetStream** — broker-side stream storage with durable consumers, explicit acks, redelivery on nack/timeout, replay from a position, and per-stream retention/replication policy.

The forces shaping the choice:

1. **Atomic-publish requirement is already satisfied upstream.** The API never publishes to NATS from a request thread; it writes a domain event to `outbox_messages` in the same transaction as the aggregate, and a Quartz job (`ProcessOutboxMessagesJob`) replays it through MediatR to `ISandboxEventPublisher.PublishSpawnAsync`. That gives **at-least-once publish** as a Postgres invariant. The publisher-side durability problem JetStream typically solves is therefore already solved by the outbox.
2. **Consumers are idempotent by design.** The Sandbox aggregate's state machine (`Provisioning → Running → Deleting → Deleted`, plus `Failed`) ignores transitions that don't apply (`MarkRunning` requires `Provisioning`; `MarkDestroyed` is idempotent on terminal states). The worker keys spawn handling on sandbox UUID. Duplicate delivery is therefore safe — but also unnecessary for correctness, since the outbox publishes each event exactly once on the happy path.
3. **Asymmetric subscription patterns.** `vm.spawn`/`vm.destroy` use queue groups (competing consumers across worker replicas). `vm.ready`/`vm.destroyed` use queue groups for the API but **fanout** (no queue group) for `alcatraz.routes`, because every gateway replica needs the full registry. JetStream supports both patterns, but each named non-queue durable would create its own per-replica consumer state on the broker.
4. **Operational scope today is small.** One API instance, one or two workers, a small number of routes replicas in the gateway profile. Stuck sandboxes are visible (`SELECT * FROM sandboxes WHERE status IN ('Provisioning','Deleting') AND created_on_utc < now() - interval '5 minutes'`) and rare. The current rate of incidents that JetStream would prevent does not yet justify operating it.
5. **Failure modes that core NATS allows but JetStream would prevent.**
   - No worker connected at publish time → `vm.spawn` is dropped at the broker; outbox row is already marked processed; sandbox stuck in `Provisioning`. **No reaper today.**
   - Worker crashes mid-`vm.Spawn()` → same outcome. No redelivery.
   - API crashes mid-`vm.ready` handler → message is auto-acked on receipt, never reprocessed; sandbox stuck in `Provisioning` even though the VM is up.
   - Routes replica down when `vm.ready` is published → that replica's registry is missing the entry until the *next* state-change for that sandbox; for a long-lived sandbox that may be never.
6. **JetStream operational cost.** Each stream needs explicit configuration (retention policy, max bytes, replication factor, storage tier), each durable consumer needs its own config (ack wait, max deliver, deliver policy), and the broker must persist messages to disk and (for HA) replicate them. This is real ops surface: stream config drift, ack-wait tuning, storage filling up, replica resync. None of that work has a clear payoff while the failure-mode incidents above remain rare.

## Decision

Use **core NATS** for the four lifecycle subjects (`vm.spawn`, `vm.destroy`, `vm.ready`, `vm.destroyed`). The outbox is the single durability mechanism for command publishes; consumer-side losses are a known, observable failure mode that operators recover from manually for now.

The broker container runs with `-js` enabled (`docker-compose.yml:98`); the `ALCATRAZ_USAGE` JetStream stream and its two pull consumers are declared at API startup by `JetStreamProvisioningHostedService`. See [ADR-0012](0012-jetstream-for-billing-subjects.md) for the carve-out rationale — in short, billing data has no upstream outbox and no in-DB reconciliation, so the trade-offs that justify core NATS for lifecycle do not hold for usage events.

## Consequences

### Positive

- **Zero broker-side state to operate.** No stream definitions, no consumer configs, no storage tier, no replica tuning. Restarting `alcatraz-nats` requires no recovery procedure.
- **One durability story to reason about.** "Did the message get published?" is answered by the `outbox_messages` row, not by a combination of outbox + stream + consumer position.
- **Subscriber code stays minimal.** No ack management, no redelivery dedup beyond the natural idempotency the aggregate already provides, no consumer offset bookkeeping. The whole `VmReadyConsumer` fits on one screen because of this.
- **Fanout for routes is free.** Adding a routes replica means `nc.Subscribe(...)` on startup; no new durable consumer to provision on the broker per replica.
- **Reversible.** Adopting JetStream later is a publisher- and consumer-side change but not a topology rewrite — the subjects and payloads stay the same.

### Negative

- **No recovery for messages lost in the consumer-down window.** A sandbox stuck in `Provisioning` because no worker was connected when `vm.spawn` was published, or because the API was down when `vm.ready` arrived, stays stuck until an operator updates the row. We have no reaper job today (only `ProcessOutboxMessagesJob` runs in the background — see [`../nats-broker.md`](../nats-broker.md) §9).
- **Slow-consumer drops are silent to the domain.** Worker subscribers use a 64-message buffered channel (`alcatraz.worker/internal/messaging/subscriber.go:42`); if it overflows, NATS will log a slow-consumer warning and discard messages. Nothing in the Sandbox state machine notices.
- **No replay for new subscribers.** A newly added service that wants to consume `vm.ready` history cannot — the events are gone. This forecloses event-sourcing-style backfills until JetStream is in.
- **No DLQ.** A consumer that throws on every redelivery just logs per attempt; there is no parking lot subject for poison messages. Acceptable today only because there is no broker-driven redelivery to begin with.
- **The outbox guarantee is asymmetric.** The publish side is at-least-once; the consume side is at-most-once. Operators must hold both halves in their head when triaging incidents.

### When to revisit

Promote the **lifecycle** `vm.*` subjects to JetStream when **any** of the following becomes true:

- We see more than a handful of stuck-sandbox incidents per month attributable to consumer-down windows.
- ~~We add a third bounded context that consumes `vm.ready`~~ — happened; resolved by carving out billing onto its own stream rather than migrating lifecycle. See [ADR-0012](0012-jetstream-for-billing-subjects.md).
- We need to scale `alcatraz.api` horizontally with strong "exactly one replica processes each `vm.ready`" guarantees (the queue group already handles this for liveness, but JetStream gives us redelivery if the chosen replica crashes mid-handler).
- We adopt schema versioning that requires holding the *prior* version's events long enough for migration.

The migration path remains incremental: declare a stream that captures `vm.spawn`/`vm.destroy`/`vm.ready`/`vm.destroyed`, point new durable consumers at it on the API and routes side, leave the worker on core NATS for the command direction (the outbox already covers that), then deprecate the core subscriptions. The billing stream (`ALCATRAZ_USAGE`) is independent and stays as-is.
