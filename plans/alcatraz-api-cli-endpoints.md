# Plan: alcatraz.api endpoints for the alcatraz CLI

## Context

`alcatraz.api` was just added as the customer-facing control plane. The end-state customer access design (`plans/customer-vm-access-ssh-ca.md`) says the CLI authenticates via OAuth 2.0 device-code flow against Keycloak, requests a sandbox, and exchanges its workstation pubkey for a short-lived SSH user certificate. Today `alcatraz.api` has Keycloak JWT bearer wired up but **no device flow**, **no sandbox aggregate**, and **no SSH CA** — it can validate tokens once Keycloak hands them out, but nothing the CLI needs in order to *get* a token or a VM.

This plan adds those endpoints. After this round, the CLI can: log in via device flow → list/create/delete sandboxes → request an SSH cert for a sandbox. Worker spawn is dispatched via the existing `vm.spawn` NATS subject. The gateway and worker callback (endpoint reporting) remain deferred per the SSH-CA plan's scope.

## Locked-in decisions

- **API proxies Keycloak's device flow.** CLI never knows realm/client_id; secret stays server-side. Reuse the existing `alcatraz-auth-client`.
- **Aggregate name: `Sandbox`.** Worker term "VM" stays inside the worker; API uses `sandbox`/`sandboxes` everywhere.
- **Worker dispatch: NATS publish only.** Use existing `vm.spawn` subject + `CreateVirtualMachineInput` payload (`alcatraz.worker/internal/vm/config.go:50-55`). Define `vm.destroy` with `{id}` as a forward contract — worker subscriber is a follow-up.
- **SSH cert signing: shell out to `ssh-keygen -s`.** `openssh-client` binary in the API container, no hand-rolled wire format.
- **Scope:** device flow + sandbox CRUD + connect. KRL admin + CA rotation deferred. KRL endpoint is **not** included this round.

## Endpoints

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/api/v1/auth/device` | anon | Initiate device flow, return `{device_code, user_code, verification_uri, verification_uri_complete, expires_in, interval}` |
| POST | `/api/v1/auth/device/token` | anon | Poll for token; body `{deviceCode}`; on success returns `{access_token, refresh_token, expires_in, token_type, id_token?}`; pending/slow_down/expired/denied → 400 + ProblemDetails extension `{error: "..."}` (RFC 8628 codes) |
| POST | `/api/v1/sandboxes` | bearer | Create sandbox; body `{vcpus, memoryMib}`; 201 + `SandboxResponse` |
| GET | `/api/v1/sandboxes` | bearer | List caller's sandboxes (excluding Deleted) |
| GET | `/api/v1/sandboxes/{id}` | bearer | Get one (owner-scoped, 404 on miss *or* not-yours — never leak existence) |
| DELETE | `/api/v1/sandboxes/{id}` | bearer | Mark Deleting, publish destroy; 202 |
| POST | `/api/v1/sandboxes/{id}/ssh-cert` | bearer | Body `{sshPubkey}`; 200 + `{cert, validUntilUtc, gatewayHost, gatewayPort}` |

## Domain model

`src/Alcatraz.Domain/Sandboxes/`:

```csharp
public sealed class Sandbox : Entity
{
    public Guid OwnerUserId { get; private set; }
    public int RequestedVcpus { get; private set; }
    public int RequestedMemoryMib { get; private set; }
    public SandboxStatus Status { get; private set; }
    public DateTime CreatedOnUtc { get; private set; }
    public DateTime? DeletedOnUtc { get; private set; }

    public static Sandbox Request(Guid ownerUserId, int vcpus, int memoryMib, DateTime utcNow);
    public Result MarkDeleting(DateTime utcNow);
    public Result EnsureOwnedBy(Guid userId); // returns SandboxErrors.NotFound on mismatch
}

public enum SandboxStatus { Provisioning = 1, Running = 2, Deleting = 3, Deleted = 4, Failed = 5 }
//   Running/Failed are reserved for the future worker callback; this round only sets
//   Provisioning, Deleting, Deleted.
```

Owner is the local `UserId` Guid (matches existing FK convention). The Keycloak `sub` (`IdentityId`) is read from `IUserContext` only when constructing the cert's `key_id`.

`SandboxErrors`: `NotFound`, `AlreadyDeleting`, `AlreadyDeleted`, `InvalidStateForCertIssue`.

Domain events (raised in aggregate, dispatched post-commit by the existing outbox processor):
- `SandboxRequestedDomainEvent(Guid SandboxId, Guid OwnerUserId, int Vcpus, int MemoryMib)` → handler publishes to `vm.spawn`.
- `SandboxDeletionRequestedDomainEvent(Guid SandboxId)` → handler publishes to `vm.destroy`.

`ISandboxRepository` exposes `GetByIdAsync`, `Add` only — listing is Dapper in the query handler (mirrors `GetBookingQueryHandler`).

## Application layer

`src/Alcatraz.Application/`:

- `Abstractions/Authentication/IDeviceAuthorizationClient.cs` — `InitiateAsync`, `ExchangeAsync(deviceCode)`. Both return `Result<T>`. Exchange propagates RFC 8628 errors as `Auth.Device.AuthorizationPending`, `…SlowDown`, `…ExpiredToken`, `…AccessDenied`.
- `Abstractions/Messaging/ISandboxEventPublisher.cs` — `PublishSpawnAsync(id, ownerUserId, vcpus, memoryMib, ct)`, `PublishDestroyAsync(id, ct)`.
- `Abstractions/Security/ISshCertificateAuthority.cs` — `Issue(sshPubkeyOpenSsh, principal, ttl, keyId, utcNow) → Result<IssuedSshCertificate>` returning `(CertOpenSsh, ValidAfterUtc, ValidUntilUtc)`.
- Feature folders under `Sandboxes/`: `InitiateDeviceAuth/`, `ExchangeDeviceToken/`, `CreateSandbox/`, `DeleteSandbox/`, `IssueSshCertificate/`, `GetSandbox/`, `ListSandboxes/`. Each contains the command/query record, validator (FluentValidation), and handler. Domain handlers (`SandboxRequestedDomainHandler`, `SandboxDeletionRequestedDomainHandler`) live alongside their triggering use-case folders.

Validation rules:
- `CreateSandbox`: `1 ≤ vcpus ≤ 16`, `512 ≤ memoryMib ≤ 32768`, `memoryMib % 256 == 0`.
- `IssueSshCertificate`: pubkey non-empty, ≤ 4096 chars, prefix in `{ssh-ed25519 , ecdsa-sha2-nistp{256,384,521} , ssh-rsa }`. Full parse is delegated to `ssh-keygen` itself, which errors on malformed input.
- `ExchangeDeviceToken`: `deviceCode` non-empty, length-bounded.

`IssueSshCertificateCommandHandler` builds `keyId = $"{userContext.IdentityId}:{sandboxId}:{unixTs}"`, principal = `sandboxId.ToString()`, ttl = 24h. Pubkey is **never persisted**. The handler logs issuance via `ILogger` (no domain event — nothing to commit alongside it).

## Infrastructure

`src/Alcatraz.Infrastructure/`:

- **Authentication/`KeycloakDeviceAuthorizationClient.cs`** — `IDeviceAuthorizationClient` impl over `HttpClient`. Form-encoded POST to `<BaseUrl>/realms/<Realm>/protocol/openid-connect/auth/device` (with `client_id` + `client_secret` from existing `KeycloakOptions`); token exchange to existing `TokenUrl` with `grant_type=urn:ietf:params:oauth:grant-type:device_code`. Maps Keycloak's `error` field to stable `Result.Error` codes.
- **Messaging/`NatsConnectionFactory.cs`** — singleton wrapper over `NATS.Net` (`NatsConnection`).
- **Messaging/`NatsSandboxEventPublisher.cs`** — `ISandboxEventPublisher`. Spawn payload: `{ id, vcpus, memory_mib, customer_id }` JSON serialized with `JsonNamingPolicy.SnakeCaseLower`; `customer_id = ownerUserId.ToString()`. The worker's Go struct uses `omitempty` so extra fields are safe even though it ignores `customer_id` today. Destroy payload: `{ id }`.
- **Security/`SshKeygenCertificateAuthority.cs`** — `ISshCertificateAuthority`. On `Issue`:
  1. Write pubkey to a temp file under `Path.GetTempPath()` using `Path.GetRandomFileName()`.
  2. Run `ssh-keygen -s <Options.PrivateKeyPath> -I <keyId> -n <principal> -V +1440m <tmp>.pub`. Use `Process.Start` with stderr captured.
  3. Read `<tmp>-cert.pub`, return its contents.
  4. Always delete temp files in a `finally` block. On non-zero exit, return `Result.Failure(SshErrors.SigningFailed)` with stderr in the error message (logged, not surfaced to caller).
- **Configurations/`SandboxConfiguration.cs`** — `IEntityTypeConfiguration<Sandbox>`. Table `sandboxes`. Status as `int`. FK to `users.id`. Indexes on `owner_user_id` and `status`.
- **Repositories/`SandboxRepository.cs`** — inherits the existing `Repository<T>` base.
- **Migrations/`<ts>_Add_Sandboxes.cs`** — see schema below.
- **DependencyInjection.cs** — register `NatsOptions`, `SshCertificateAuthorityOptions`, `GatewayOptions`; singleton `NatsConnectionFactory`, `ISandboxEventPublisher`, `ISshCertificateAuthority`; scoped `ISandboxRepository`; typed `HttpClient` for `IDeviceAuthorizationClient`.
- **csproj** — add `NATS.Net` (≥ 2.5).

## API layer

`src/Alcatraz.Api/Controllers/`:

- **Auth/`AuthController.cs`** — `[AllowAnonymous]`, two routes above. Maps `Auth.Device.*` errors to 400 + ProblemDetails extension `error`.
- **Sandboxes/`SandboxesController.cs`** — `[Authorize]`, the five sandbox routes. Returns 201 (create), 202 (delete), 200 (get/list/cert), 404 (`SandboxErrors.NotFound`), 400 (validation).
- **Extensions/`ResultExtensions.cs`** *(new)* — small helper `MapError(Error) → IActionResult` that the new controllers use. The existing `BookingsController`'s coarse `StatusCode(500, error)` pattern is left alone (out of scope).

DTOs: `CreateSandboxRequest(int Vcpus, int MemoryMib)`, `IssueSshCertificateRequest(string SshPubkey)`, `ExchangeDeviceTokenRequest(string DeviceCode)`. Responses are the Application-layer records returned directly.

## Configuration additions

```jsonc
"Keycloak": {
  // existing keys...
  "DeviceAuthorizationUrl": "<BaseUrl>/realms/<Realm>/protocol/openid-connect/auth/device"
},
"Nats": {
  "Url": "nats://alcatraz-nats:4222",
  "SpawnSubject": "vm.spawn",
  "DestroySubject": "vm.destroy"
},
"Ssh": {
  "CA": {
    "PrivateKeyPath": "/run/secrets/alcatraz_ca",
    "DefaultTtlHours": 24
  }
},
"Gateway": {
  "Host": "ssh.alcatraz.io",
  "Port": 443
}
```

`appsettings.Development.json` overrides for local compose.

## Migration: `Add_Sandboxes`

```
sandboxes:
  id                    uuid PRIMARY KEY
  owner_user_id         uuid NOT NULL REFERENCES users(id)
  requested_vcpus       int  NOT NULL
  requested_memory_mib  int  NOT NULL
  status                int  NOT NULL
  created_on_utc        timestamptz NOT NULL
  deleted_on_utc        timestamptz NULL
INDEX ix_sandboxes_owner_user_id ON sandboxes(owner_user_id);
INDEX ix_sandboxes_status_active ON sandboxes(status) WHERE status <> 4;
```

No customer pubkeys, no cert material — matches the SSH-CA plan's "no persisted pubkey" constraint.

## Critical files

- New: `src/Alcatraz.Domain/Sandboxes/Sandbox.cs`
- New: `src/Alcatraz.Application/Sandboxes/IssueSshCertificate/IssueSshCertificateCommandHandler.cs`
- New: `src/Alcatraz.Infrastructure/Security/SshKeygenCertificateAuthority.cs`
- New: `src/Alcatraz.Infrastructure/Messaging/NatsSandboxEventPublisher.cs`
- New: `src/Alcatraz.Infrastructure/Authentication/KeycloakDeviceAuthorizationClient.cs`
- New: `src/Alcatraz.Api/Controllers/Sandboxes/SandboxesController.cs`
- New: `src/Alcatraz.Api/Controllers/Auth/AuthController.cs`
- Modified: `src/Alcatraz.Infrastructure/DependencyInjection.cs`, `src/Alcatraz.Infrastructure/ApplicationDbContext.cs` (auto via `ApplyConfigurationsFromAssembly`), `src/Alcatraz.Infrastructure/Alcatraz.Infrastructure.csproj`
- Reference: `alcatraz.worker/internal/messaging/config.go:16` (`vm.spawn`), `alcatraz.worker/internal/vm/config.go:50-55` (`CreateVirtualMachineInput` shape), `plans/customer-vm-access-ssh-ca.md` (cert flow source of truth)

## Tests (deliverable — not optional)

Every endpoint and every domain rule below must ship with tests in this round. xUnit + NSubstitute + FluentAssertions per existing convention.

**Domain unit — `test/Alcatraz.Domain.UnitTests/Sandboxes/SandboxTests.cs`**
- `Request_SetsProvisioningStatus_AndRaisesSandboxRequestedEvent`
- `MarkDeleting_FromProvisioning_TransitionsToDeleting_AndRaisesEvent`
- `MarkDeleting_FromRunning_TransitionsToDeleting_AndRaisesEvent`
- `MarkDeleting_FromDeleting_ReturnsAlreadyDeleting`
- `MarkDeleting_FromDeleted_ReturnsAlreadyDeleted`
- `EnsureOwnedBy_WithMatchingUser_ReturnsSuccess`
- `EnsureOwnedBy_WithDifferentUser_ReturnsNotFound`

**Application unit — `test/Alcatraz.Application.UnitTests/Sandboxes/`**
- `CreateSandboxCommandHandlerTests`: builds Sandbox with command vcpus/mem + `IUserContext.UserId`; calls `Add` + `SaveChangesAsync`; returns `SandboxResponse`; failure path when `IUserContext` unauthenticated.
- `DeleteSandboxCommandHandlerTests`: missing sandbox → `NotFound`; ownership mismatch → `NotFound` (not Forbidden — no information leak); already-deleting → `AlreadyDeleting`; happy path raises `MarkDeleting` and saves.
- `IssueSshCertificateCommandHandlerTests`: ownership check; sandbox in Deleting/Deleted/Failed → `InvalidStateForCertIssue`; happy path invokes `ISshCertificateAuthority.Issue` with principal = sandbox-id string, ttl = 24h, key-id format `<identityId>:<sandboxId>:<unixTs>`; CA failure surfaces as `Result.Failure`.
- `InitiateDeviceAuthCommandHandlerTests`: forwards `IDeviceAuthorizationClient.InitiateAsync` result.
- `ExchangeDeviceTokenCommandHandlerTests`: success path returns token DTO; each RFC 8628 error code (`authorization_pending`, `slow_down`, `expired_token`, `access_denied`) propagates as the corresponding stable `Auth.Device.*` `Error`.
- `ListSandboxesQueryHandlerTests` / `GetSandboxQueryHandlerTests`: owner-scoped filtering; deleted sandboxes excluded from list; not-yours returns `NotFound`.
- Validators: one test per validator covering each rule (vcpus bounds, memory bounds + 256 multiple, pubkey prefix allowlist, deviceCode non-empty).

**Application integration — `test/Alcatraz.Application.IntegrationTests/Sandboxes/`** (real Postgres via `WebApplicationFactory`)
- `CreateSandbox_OnSuccess_WritesSandboxAndOutboxRowInSameTransaction` — assert one row in `sandboxes` and one `OutboxMessage` of type `SandboxRequestedDomainEvent` with matching `SandboxId`.
- `DeleteSandbox_WritesSandboxDeletionRequestedOutboxRow`.
- `SandboxRequestedDomainHandler_InvokesPublishSpawn` — substituted `ISandboxEventPublisher` registered through the test factory; verify call args.
- `SandboxDeletionRequestedDomainHandler_InvokesPublishDestroy`.
- `NatsSandboxEventPublisher_PayloadShape` — focused unit-style test (no broker) on the JSON serialization: snake_case keys, `id`/`vcpus`/`memory_mib`/`customer_id` present, no extras.

**API functional — `test/Alcatraz.Api.FunctionalTests/`**
- `Auth/DeviceFlowEndpointsTests.cs`: `POST /auth/device` returns the six required fields; `POST /auth/device/token` happy path returns access token; pending/expired/denied each return 400 with ProblemDetails extension `error` set to the RFC code. `IDeviceAuthorizationClient` substituted via the test factory.
- `Sandboxes/SandboxesEndpointsTests.cs`:
  - `POST /sandboxes` happy → 201 + body, persisted, outbox row written.
  - `POST /sandboxes` invalid body (vcpus 0, mem 100) → 400 with validation problem.
  - `GET /sandboxes` returns only caller's non-deleted sandboxes.
  - `GET /sandboxes/{id}` for someone else's sandbox → 404.
  - `DELETE /sandboxes/{id}` ownership mismatch → 404; happy path → 202.
  - `POST /sandboxes/{id}/ssh-cert` malformed pubkey → 400; happy path → 200; the returned cert is then verified by spawning `ssh-keygen -L -f -` and asserting `Principals: <sandbox-id>` and validity window ≈ 24h. Skipped via `[SkippableFact]` when `ssh-keygen` is absent (CI portability).
  - Unauthenticated request to any sandbox route → 401.

**Test factory wiring**
- Substitute `IDeviceAuthorizationClient` so functional tests don't need a live Keycloak.
- Substitute `ISandboxEventPublisher` so functional tests don't need a NATS broker.
- Use the real `ISshCertificateAuthority` (`SshKeygenCertificateAuthority`) with a CA key generated into the test fixture's temp dir; this keeps the cert path end-to-end honest. Skip the cert assertions when `ssh-keygen` is unavailable.

**Definition of done**
- All new code paths have at least one unit test.
- Every controller route has at least one happy-path and one failure-path functional test.
- `dotnet test` is green across all four test projects.

## End-to-end verification

```bash
# 1. Bring up infra (consolidated root-level compose; brings up postgres, redis, keycloak, nats, seq, ca-init, demo sshd)
docker compose up -d
dotnet ef database update -p alcatraz.api/src/Alcatraz.Infrastructure -s alcatraz.api/src/Alcatraz.Api

# 2. Generate CA key, point API at it
ssh-keygen -t ed25519 -f /tmp/alcatraz_ca -N ""
export Ssh__CA__PrivateKeyPath=/tmp/alcatraz_ca
dotnet run --project alcatraz.api/src/Alcatraz.Api &

# 3. Keycloak prerequisite: enable "OAuth 2.0 Device Authorization Grant"
#    on alcatraz-auth-client in the realm's admin UI.

# 4. Device flow
curl -sX POST http://localhost:5000/api/v1/auth/device | jq .
# open verification_uri_complete in browser, sign in
TOKEN=$(curl -sX POST http://localhost:5000/api/v1/auth/device/token \
  -H 'content-type: application/json' \
  -d '{"deviceCode":"<code>"}' | jq -r .access_token)

# 5. Create + list + cert
ID=$(curl -sX POST http://localhost:5000/api/v1/sandboxes \
  -H "authorization: bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"vcpus":2,"memoryMib":2048}' | jq -r .id)
curl -s http://localhost:5000/api/v1/sandboxes -H "authorization: bearer $TOKEN" | jq .

ssh-keygen -t ed25519 -f /tmp/id_alcatraz -N ""
PUB=$(cat /tmp/id_alcatraz.pub)
curl -sX POST "http://localhost:5000/api/v1/sandboxes/$ID/ssh-cert" \
  -H "authorization: bearer $TOKEN" -H 'content-type: application/json' \
  -d "{\"sshPubkey\":\"$PUB\"}" | jq -r .cert > /tmp/id_alcatraz-cert.pub
ssh-keygen -L -f /tmp/id_alcatraz-cert.pub
# Confirm: Type: user cert | Principal: <sandbox-id> | Valid: now → +24h | Signing CA: <our key>

# 6. NATS publish — separately, check the worker / a NATS CLI subscriber receives
#    the spawn message:
nats sub vm.spawn   # in another shell, before step 5

# 7. Delete
curl -sX DELETE "http://localhost:5000/api/v1/sandboxes/$ID" -H "authorization: bearer $TOKEN" -i
# expect 202; nats sub vm.destroy receives {"id":"<id>"}
```

## Deferred / out of scope this round

- **Worker → API endpoint callback** (`{worker_host, vm_ip, ssh_user, ssh_port}` reporting). Sandbox stays `Provisioning`. Recommend a future `sandbox_endpoints` side-table rather than columns on `sandboxes` so the aggregate stays stable.
- **KRL endpoint** (`GET /v1/ssh/krl`) — gateway-side and worker-side polling not yet implemented; nothing to revoke yet.
- **CA key encryption at rest.** v1 reads from a config-supplied path (mounted Docker secret / KMS-decrypted volume). KMS/Vault integration via an `ISshCaKeyProvider` is a follow-up.
- **NATS publish-failure handling vs. existing outbox semantics.** The current `ProcessOutboxMessagesJob` marks errored rows as processed; for a hard NATS broker outage we'd lose the spawn signal. Mitigations (retry-on-error sweep job, or buffered publish in `NatsSandboxEventPublisher`) deferred — flag as a known gap.
- **Cert audit trail.** Issuance is logged but not persisted. Add an `ssh_certificate_issuances` table later if compliance requires it.
- **Public Keycloak CLI client.** Reusing the confidential `alcatraz-auth-client` is fine because the API holds the secret. If a future client (browser-side, IDE extension) needs direct Keycloak access, mint a separate public `alcatraz-cli` client then.
