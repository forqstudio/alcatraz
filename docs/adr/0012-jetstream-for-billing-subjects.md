# ADR-0012: JetStream for billing subjects (carve-out from ADR-0001)

- **Status:** Accepted
- **Date:** 2026-05-12
- **Deciders:** Alcatraz core team
- **Related:** [`0001-core-nats-over-jetstream.md`](0001-core-nats-over-jetstream.md), [`../nats-broker.md`](../nats-broker.md), [`../billing-metrics.md`](../billing-metrics.md), [`../../plans/billing-usage-metering.md`](../../plans/billing-usage-metering.md)

## Context

[ADR-0001](0001-core-nats-over-jetstream.md) decided that all four lifecycle subjects (`vm.spawn`, `vm.destroy`, `vm.ready`, `vm.destroyed`) ride core NATS, with the transactional outbox carrying publish-side durability and the Sandbox aggregate's idempotent state machine absorbing duplicate / missing deliveries. ADR-0001 § "When to revisit" listed one explicit trigger for this revisit:

> We add a third bounded context that consumes `vm.ready` (e.g. a billing/usage projector) and would want history on first deploy.

That third bounded context is billing. The metering pipeline (see [`../billing-metrics.md`](../billing-metrics.md)) introduces two new subjects, `vm.usage_sample` and `vm.usage_final`, with delivery requirements that are materially different from lifecycle:

1. **No publish-side outbox.** The publisher is the worker, not the API. There is no Postgres transaction wrapping the publish, so the outbox-as-durability story from ADR-0001 does not apply. If the API is down at publish time, a core-NATS message is lost with no recovery path.
2. **No idempotent reconciliation from elsewhere.** Lifecycle is self-healing: a missed `vm.ready` leaves the sandbox stuck in `Provisioning`, which is observable in the DB and recoverable by re-running the worker → API state machine. A missed `vm.usage_sample` has **no** other source of truth — there is no DB row anywhere that says "this minute happened." Lost samples are lost revenue.
3. **At-least-once is acceptable; at-most-once is not.** Billing handlers can be made idempotent cheaply (DB unique index on `(sandbox_id, sampled_at_utc)`, PK on `sandbox_id` for finals). Duplicates are absorbed at the DB layer. The expensive failure mode is dropping a message, not duplicating one.
4. **Producer-consumer ack semantics matter.** The API must persist the row before acknowledging — otherwise an API crash mid-handler creates a "we acked, the DB doesn't have it" gap. JetStream's explicit-ack model fits this directly; core NATS auto-acks on receipt and offers no recovery.
5. **Storage cost is bounded.** Interest-based retention drops messages once every registered consumer has acked. Two consumers exist (`usage-sample-consumer`, `usage-final-consumer`); both ack after DB commit. Steady-state stream depth is near zero except during an API outage.

The remaining lifecycle subjects from ADR-0001 do not gain enough from JetStream to justify the parallel migration. Their failure modes (stuck sandboxes) are observable in the DB and rare; their idempotency is well-established in the aggregate. They stay on core NATS.

## Decision

Use **JetStream** for the two new billing subjects (`vm.usage_sample`, `vm.usage_final`). Keep **core NATS** for the four lifecycle subjects (`vm.spawn`, `vm.destroy`, `vm.ready`, `vm.destroyed`) per ADR-0001.

Stream configuration:

| Setting             | Value                                                       |
| ------------------- | ----------------------------------------------------------- |
| Name                | `ALCATRAZ_USAGE`                                            |
| Subjects            | `vm.usage_sample`, `vm.usage_final`                         |
| Storage             | File (`docker-compose.yml` mounts `nats_jetstream:/data/jetstream`) |
| Retention           | Interest                                                    |
| Discard             | Old                                                         |
| MaxMsgSize          | 64 KiB                                                      |
| Replicas            | 1                                                           |

Consumers:

| Name                    | Filter            | AckPolicy | AckWait | MaxDeliver |
| ----------------------- | ----------------- | --------- | ------- | ---------- |
| `usage-sample-consumer` | `vm.usage_sample` | Explicit  | 30 s    | 5          |
| `usage-final-consumer`  | `vm.usage_final`  | Explicit  | 60 s    | 10         |

Both stream and consumers are declared idempotently at API startup by `JetStreamProvisioningHostedService` (`alcatraz.api/src/Alcatraz.Infrastructure/Messaging/JetStreamProvisioningHostedService.cs`).

**Server-side dedup:** every publish carries a `Nats-Msg-Id` header — `"{sandbox_id}|{sampled_at_utc.UnixNano()}"` for samples, `sandbox_id` for finals — so redeliveries within the stream's 2-minute dedup window are absorbed by the broker. The DB layer provides a second line of defence (unique index on samples, PK on records).

**Ordering guarantee:** the worker publishes `vm.usage_final` before `vm.destroyed`. Final is awaited synchronously via JetStream PubAck; destroyed flushes via core NATS. The API can therefore process `vm.destroyed` knowing that, if a usage record is coming, it is already durably stored.

## Consequences

### Positive

- **Billing data is not silently dropped.** API outages are absorbed by JetStream's file-backed storage; consumers drain on reconnect.
- **Ack-after-DB-commit closes the consumer-loss gap.** A handler crash after JetStream fetch but before DB commit results in redelivery, not silent loss.
- **Interest-based retention keeps storage bounded.** Steady-state stream depth is near zero; cost grows only with API outage duration.
- **No change to ADR-0001's lifecycle reasoning.** Lifecycle delivery semantics are unchanged; only the two new billing subjects move.
- **Belt-and-suspenders idempotency.** Server-side `Nats-Msg-Id` dedup + DB constraints means duplicates are absorbed at two layers.

### Negative

- **Two delivery models in the same broker.** Operators must hold both in their head when triaging incidents. Mitigated by the canonical reference in [`../nats-broker.md`](../nats-broker.md) listing every subject and its delivery semantics.
- **JetStream storage is now production state.** `nats_jetstream` volume must be backed up alongside Postgres. Wiping it loses any usage messages in flight at the time.
- **Poison messages have no DLQ today.** A message that fails `MaxDeliver` times lands in `$JS.EVENT.ADVISORY.CONSUMER.MAX_DELIVERIES` but nothing consumes that subject. Operators triage by tailing the API logs. Monitoring that advisory subject is a follow-up.
- **Single-replica stream.** Loses recently-acked messages on broker disk failure. Acceptable for v1 (single-node NATS); revisit when we move to multi-replica.

### When to revisit

Promote lifecycle subjects to JetStream when the original ADR-0001 triggers fire — none yet have. Add a DLQ consumer for `$JS.EVENT.ADVISORY.CONSUMER.MAX_DELIVERIES` when a poison-message incident actually happens, not before. Move to multi-replica JetStream when we run more than one NATS node.
