# ADR-0006: Self-hosted Keycloak as identity provider with device-flow proxy

- **Status:** Accepted
- **Date:** 2026-05-10
- **Related:** [`0004-customer-ssh-access.md`](0004-customer-ssh-access.md), [`0009-keycloak-idempotent-user-registration.md`](0009-keycloak-idempotent-user-registration.md)

## Context

The CLI is distributed to **external paying customers** (constraint 4 — frictionless client). It can't hold privileged credentials: anything baked into the binary can be extracted. The API needs a way to authenticate those customers that:

- Doesn't require the API to handle passwords, password hashing, MFA, account lockout, or social-login federation.
- Supports OAuth 2.0 Device Authorization Grant (RFC 8628), since the CLI runs on a workstation without a guaranteed browser context.
- Keeps the auth perimeter inside our trust boundary — leaking customer auth state to a SaaS IdP is a non-starter.
- Survives `client_secret` rotation without re-shipping the CLI.

Building any of this in-house would be expensive *and* a foot-gun (timing attacks on password compare, broken MFA recovery, weak session invalidation are all classic own-goals).

## Decision

Run **self-hosted Keycloak 25** as the customer identity provider, and **proxy the device flow through the API** so the CLI never holds the `client_secret`.

Setup:

- Realm `alcatraz` with the confidential OIDC client `alcatraz-auth-client`. Device authorization grant enabled.
- The realm export at `alcatraz.api/.files/alcatraz-realm-export.json` is the source of truth for local dev (Keycloak imports it on first boot).
- The API caches Keycloak's JWKS for bearer-token verification (`iss`, `aud`, `exp`, signature).

### 1. Device-flow proxy (CLI → API → Keycloak)

Three anonymous endpoints on the API mirror Keycloak's device flow:

| Endpoint                            | Purpose                                                                                              |
| ----------------------------------- | ---------------------------------------------------------------------------------------------------- |
| `POST /api/v1/auth/device`          | Initiate; returns `device_code, user_code, verification_uri, verification_uri_complete, expires_in, interval` |
| `POST /api/v1/auth/device/token`    | Poll; `authorization_pending` / `slow_down` / `expired_token` / `access_denied` map to RFC 8628 codes via ProblemDetails extension `error` |
| `POST /api/v1/auth/refresh`         | Silent token refresh; the CLI's `BearerHandler` calls it on proactive refresh (within 60s of expiry) and on `401` |

`KeycloakDeviceAuthorizationClient` is the typed `HttpClient` that wraps these calls; it forwards Keycloak's `error` field verbatim so RFC 8628 semantics survive the proxy. CLI tokens cache at `~/.config/alcatraz/tokens.json` (mode `0600`); the workstation ed25519 keypair at `~/.config/alcatraz/id_alcatraz` is generated on first cert request and reused.

### 2. Admin API for user registration (API → Keycloak admin)

`POST /api/v1/users/register` creates the user in Keycloak via its admin REST API, then mirrors a row into the local `users` table keyed on Keycloak's `sub` (`identity_id`). The admin client uses a service-account token attached by `AdminAuthorizationDelegatingHandler`. **Splitting this way means:** Keycloak owns the credential (we never store passwords); the local `users` row owns the foreign-key target for sandbox ownership and the role/permission edges; the two are reconciled on `409` (see [ADR-0009](0009-keycloak-idempotent-user-registration.md)).

`AdminAuthorizationDelegatingHandler` does not call `EnsureSuccessStatusCode` — callers check status themselves so `AuthenticationService` can branch on Keycloak's 409. The handler's contract is documented in its XML doc; only one client (`AuthenticationService`) is registered against it today, and any future registration must remember to check status itself.

### 3. Authn/authz on protected endpoints

JWT bearer middleware verifies Keycloak-issued tokens. A claims transformation joins the user's roles → permissions on every request. The custom `[HasPermission(...)]` attribute gates controllers at the **permission** level, not the role level — roles are an indirection so permission edges can be edited without re-issuing tokens. `IUserContext` exposes the resolved local `UserId` + Keycloak `IdentityId` to handlers; sandbox ownership uses `UserId`, while the SSH cert's `key_id` includes `IdentityId` for cross-system audit (see [ADR-0004](0004-customer-ssh-access.md)).

## Consequences

### Positive

- **API never sees a customer password.** Passwords, MFA, account lockout, federation are entirely Keycloak's job.
- **No `client_secret` in the CLI.** Rotating Keycloak realm or client config does not require re-shipping the CLI.
- **Self-hosted = inside the trust boundary.** No per-user vendor cost, no third-party data-residency concerns.
- **Permission-level authorization** lets us re-edge permissions without invalidating active tokens.
- **Two well-defined trust hops.** Customer ↔ Keycloak (device flow), Keycloak ↔ API (JWT validation). Each is a standard.

### Negative

- **Two stateful systems to back up.** Keycloak's volume *and* the API DB are both load-bearing for authentication.
- **`AdminAuthorizationDelegatingHandler` is shared mutable contract.** Removing `EnsureSuccessStatusCode` means new callers can silently fail-open if they forget to check status.
- **Realm-import drift is unguarded.** A wipe-and-reimport on Keycloak doesn't currently diff against the live realm — see `plans/open-issues.md`.
- **Self-hosted means we own Keycloak upgrades.** Keycloak 25 → 26 will eventually be a planned migration.
