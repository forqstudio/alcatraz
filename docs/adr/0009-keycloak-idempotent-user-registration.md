# ADR-0009: User registration is idempotent against Keycloak

- **Status:** Accepted
- **Date:** 2026-05-10
- **Related:** [`0006-keycloak-identity-provider.md`](0006-keycloak-identity-provider.md)

## Context

Keycloak and the API DB are separate volumes; either can be wiped independently — and routinely *are* in dev. Without reconciliation, a wiped API DB locks the customer out: Keycloak still has them (so they can authenticate), but the local `users` row is missing (so sandbox ownership has no foreign-key target, and `POST /users/register` fails with a duplicate-username error from Keycloak's side).

Reproducing this on a teammate's machine — or worse, on a recovered prod DB — should not require admin intervention.

## Decision

`POST /api/v1/users/register` reconciles the orphan case automatically.

Flow:

1. The API attempts to create the user in Keycloak via the admin API.
2. On Keycloak `409 Conflict` (user already exists), the API queries `GET /users?email=&exact=true` for the existing `identityId`.
3. The API then either returns the matching local row (if one exists) or inserts a fresh local row keyed on that `identityId`.

Net effect: registration is **idempotent against Keycloak state**. Whether the customer is brand new, or exists in Keycloak only, or exists in both, the same call succeeds and ends with `(keycloak user, local users row)` consistent.

`AdminAuthorizationDelegatingHandler` does **not** call `EnsureSuccessStatusCode` — callers check status themselves so `AuthenticationService` can branch on Keycloak's 409. The handler's contract is documented in its XML doc; only one client (`AuthenticationService`) is registered against it today, and any future registration must remember to check status itself (see [ADR-0006](0006-keycloak-identity-provider.md) for the broader auth design).

## Consequences

### Positive

- **Registration doubles as recovery.** A wiped API DB heals on the next customer login attempt. No admin intervention needed.
- **Dev-cycle reality is supported.** Wiping a Postgres volume during a refactor doesn't require also wiping Keycloak state.
- **Single endpoint, single semantic.** `POST /users/register` is the same call whether the customer is new, returning, or recovering — the API picks the right branch.

### Negative

- **`AdminAuthorizationDelegatingHandler` is shared mutable contract.** The "callers check status themselves" rule is enforced only by code review and an XML doc comment. A future caller that forgets it can silently fail-open.
- **Email is the reconciliation key.** A customer who changes their email at Keycloak's side without going through the API would orphan again on next register. Acceptable today because email-change isn't a customer-facing flow.
- **Two round-trips on the orphan path.** `POST /users` → `409`, then `GET /users?email=&exact=true`. Negligible at customer-login rates; would matter under attack pressure (mitigation: rate-limit anonymous registration).
