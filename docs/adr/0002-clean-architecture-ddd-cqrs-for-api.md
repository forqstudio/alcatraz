# ADR-0002: Clean Architecture, DDD, and CQRS for `alcatraz.api`

- **Status:** Accepted
- **Date:** 2026-05-10
- **Supersedes:** —
- **Related:** [`0003-transactional-outbox.md`](0003-transactional-outbox.md), [`../../plans/architecture.md`](../../plans/architecture.md)

## Context

`alcatraz.api` is the control plane. It carries:

- Heavy domain logic — sandbox lifecycle state machine (`Provisioning → Running → Deleting → Deleted`, plus `Failed`), role/permission model, SSH certificate issuance.
- A wide read surface — list, get, owner-scoping, filtering for the CLI.
- A side-effect that must be transactionally consistent with database writes — publishing `vm.spawn` / `vm.destroy` to NATS exactly when the sandbox row exists or transitions.

A pragmatic single-layer design (controllers calling DbContext directly) buckles under any of these in isolation; with all three at once, the failure modes compound: business rules leak into controllers, read paths drag transactional baggage into list endpoints, and "publish to NATS after save" becomes unreliable when the save and the publish aren't co-transactional.

## Decision

Build `alcatraz.api` on three reinforcing patterns:

1. **Clean Architecture with dependencies pointing inward.** Four projects, no upward references:
   - `Alcatraz.Domain` — aggregates, value objects, domain events, repository interfaces, error catalogues. **No external dependencies.**
   - `Alcatraz.Application` — commands and queries via MediatR, FluentValidation validators, infrastructure abstractions (`ISshCertificateAuthority`, `ISandboxEventPublisher`, `IDeviceAuthorizationClient`, …).
   - `Alcatraz.Infrastructure` — EF Core configurations + repositories, NATS publish/subscribe, Keycloak OIDC + admin clients, `ssh-keygen` shell-out.
   - `Alcatraz.Api` — controllers (primary constructors, `ISender`), permission attributes, ProblemDetails middleware.

2. **DDD aggregates with the Result pattern.**
   - Aggregates are `sealed class : Entity` with private parameterised + parameterless (EF) constructors, private setters, static factory methods (e.g. `Sandbox.Request`), and state-transition methods that return `Result` and `RaiseDomainEvent` on success.
   - Business-rule violations return `Result.Failure(error)`; never throw. Errors are static instances on `*Errors` classes (`SandboxErrors.NotFound`, `SandboxErrors.AlreadyDeleting`, …). Controllers map `IsSuccess` / `IsFailure` to HTTP status.
   - `IDateTimeProvider` injects `utcNow` so domain logic stays deterministic in tests.

3. **CQRS via MediatR.**
   - Commands are `record : ICommand<T>` paired with `*CommandHandler` and `*CommandValidator`. Validators run via a MediatR pipeline behaviour before the handler.
   - Queries are `record : IQuery<T>` returning DTOs (not domain entities). Read-heavy ones use Dapper directly; cacheable ones implement `ICachedQuery`.
   - Feature folders are grouped by aggregate + use case (`Application/Sandboxes/CreateSandbox/{Command,Handler,Validator}.cs`), not by technical concern.

## Consequences

### Positive

- **Domain logic stays testable in isolation.** xUnit + NSubstitute + FluentAssertions, no DI container, no EF, no NATS. The aggregate state machine is exercised against pure domain types.
- **Read paths stay fast.** Dapper inside query handlers without polluting write paths or domain types.
- **Infrastructure is replaceable.** Substituting `ISandboxEventPublisher` and `IDeviceAuthorizationClient` is what makes `WebApplicationFactory` functional tests work without a real NATS or Keycloak.
- **Errors are catalogued.** Static `*Errors` classes give a single source of truth for failure codes that controllers and tests both reference.

### Negative

- **Indirection cost.** A new endpoint touches Domain + Application (+ Validator) + Infrastructure (+ Configuration) + API. Onboarding requires reading the layer rules before adding the first handler.
- **Result vs. exceptions split.** Business rule failures use `Result`; infrastructure failures still throw. Reviewers must police the boundary.
- **MediatR adds a runtime hop.** Acceptable here, but profiling for hot read paths sometimes ends in "skip MediatR, call the query handler directly."

### Locked-in conventions

- Aggregate factory methods (`Sandbox.Request`, `Sandbox.Confirm`, …) raise the matching domain event on success; consumers must not new-up aggregates directly.
- `RaiseDomainEvent` is the only legal way for an aggregate to surface state changes; the EF interceptor in [ADR-0003](0003-transactional-outbox.md) depends on that contract.
- Controllers may not call repositories or DbContext directly — every write goes through a command, every read through a query.
