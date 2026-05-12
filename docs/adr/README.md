# Architecture Decision Records

Each ADR captures one shipped decision: the context that forced it, the decision itself, and the consequences (positive, negative, and when to revisit). The full architectural overview — Alcatraz's product context, hard constraints, components, and locked-in parameters — lives in [`../../plans/architecture.md`](../../plans/architecture.md), which indexes the ADRs.

Speculative work-not-yet-shipped lives in [`../../plans/open-issues.md`](../../plans/open-issues.md), not here.

## Index

| #    | Title                                                                              | Decision (one line)                                                                       |
| ---- | ---------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| 0001 | [Core NATS over JetStream](0001-core-nats-over-jetstream.md)                       | Use core NATS for lifecycle `vm.*` subjects; outbox provides publish-side durability. Carved out for billing by 0012. |
| 0002 | [Clean Architecture, DDD, CQRS for `alcatraz.api`](0002-clean-architecture-ddd-cqrs-for-api.md) | Four-layer Clean Architecture; aggregates with Result pattern; CQRS via MediatR.        |
| 0003 | [Transactional outbox](0003-transactional-outbox.md)                               | Domain events serialise into `outbox_messages` in the same tx as the aggregate write.     |
| 0004 | [Customer SSH access](0004-customer-ssh-access.md)                                 | SSH CA + per-sandbox principals + SNI gateway for cryptographic per-tenant isolation.     |
| 0005 | [NATS as the only API ↔ worker coupling](0005-nats-as-api-worker-coupling.md)      | API and worker are strangers; four NATS subjects carry the entire VM lifecycle.           |
| 0006 | [Self-hosted Keycloak](0006-keycloak-identity-provider.md)                         | Keycloak as IdP; API proxies the device flow so the CLI never holds `client_secret`.      |
| 0007 | [AgentFS overlay writes pre-boot](0007-agentfs-overlay-pre-boot.md)                | Per-VM principals + CA pubkey land in the overlay before `m.Start()`; failure aborts spawn. |
| 0008 | [Rootfs init = `tini → sshd`](0008-rootfs-init-tini-sshd.md)                       | sshd is PID 1's only child; sshd exit panics the kernel; no "running but broken" state.   |
| 0009 | [Idempotent Keycloak user registration](0009-keycloak-idempotent-user-registration.md) | `POST /users/register` reconciles on Keycloak `409`; registration doubles as recovery.   |
| 0010 | [Single root `docker-compose.yml`](0010-single-root-docker-compose.md)             | One compose file at the repo root; per-environment variation via Compose profiles.        |
| 0011 | [Devcontainer scope = code + build only](0011-devcontainer-scope.md)               | Devcontainer covers toolchains and build; KVM/Docker/worker stay on the host.             |
| 0012 | [JetStream for billing subjects](0012-jetstream-for-billing-subjects.md)           | Two new usage subjects ride JetStream with explicit ack-after-DB-commit; lifecycle stays on core NATS. |

## Authoring conventions

- **File name:** `NNNN-kebab-case-title.md` (zero-padded 4-digit sequence).
- **Format:** Status, Date, Related, Context, Decision, Consequences (Positive / Negative / When to revisit).
- **Numbering is monotonic;** never reuse a retired number. Mark superseded ADRs with `Status: Superseded by NNNN` and a one-line summary at the top.
- **Cross-link** to other ADRs and to the broader docs (`../nats-broker.md`, `../../plans/`, `../../alcatraz.api/docs/`) — readers should be able to follow rationale across decisions without re-deriving it.
- **Keep ADRs descriptive of what's shipped.** Forward-looking notes belong in `When to revisit`; not-yet-shipped work belongs in `plans/open-issues.md`.
