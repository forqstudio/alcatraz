# 2026-05-09 — End-to-end SSH-into-Firecracker debug

Wrap-up of the session that took the `dotnet alcatraz ssh $ID` happy path from
"Could not resolve hostname 172.16.0.11/24" to a clean
Provisioning → Running → Deleting → Deleted lifecycle with a real shell inside
the microVM. Five bugs surfaced and were fixed.

## Final state

```
$ dotnet "$ALC" sandbox create --vcpus 2 --memory 2048 --json   # status=1 Provisioning
$ dotnet "$ALC" sandbox ssh-cert "$ID"                           # status=2 Running, gateway=172.16.0.10:22
$ ssh ... al@172.16.0.10                                         # SSH_OK / al / Linux 6.1.166
$ dotnet "$ALC" sandbox delete "$ID"                             # status=3 Deleting
... ~10s ...                                                     # status=4 Deleted (auto-transition via vm.destroyed)
```

## Bugs fixed (in fix order)

### 1. Worker reported VM IP in CIDR form

**File:** `alcatraz.worker/internal/vm/spawn.go:182`

Worker called `ipConf.IPAddr.String()` on a `net.IPNet`, which returns
`172.16.0.11/24`. That value flowed into NATS `vm.ready`, into the API's
`sandboxes.host` column, and into `ssh-cert`'s `gatewayHost`. SSH then tried
to resolve it as a hostname and failed.

**Fix:** `ipConf.IPAddr.IP.String()` — the bare `net.IP`, no mask suffix.

### 2. Rootfs `/init` exits seconds after sshd starts → kernel panic → VM destroyed

**File:** `alcatraz.core/rootfs/init`

The serial-console init script started sshd as a daemon (good), then exec'd
`/bin/bash -l` on `ttyS0`. Firecracker's serial console doesn't attach an
interactive stdin, so bash hit immediate EOF and exited. PID 1 exiting on
Linux + `panic=1 reboot=k` → kernel panic → Firecracker exits → CNI tears
down the bridge. SSH attempts within ~10s of `vm.ready` saw "no route to
host" because the bridge was already gone.

**Fix:** replaced the interactive-bash tail of the script with `exec sleep
infinity`. Customer access is via SSH; the console is not user-facing.

### 3. sshd_config didn't enable certificate trust

**File:** `alcatraz.core/rootfs/etc/ssh/sshd_config.d/alcatraz.conf`

The worker was correctly planting `/etc/ssh/auth_principals/al` (the sandbox
UUID) and `/etc/ssh/trusted_user_ca_keys` (the API's CA pubkey) into each
VM's overlay. But `sshd_config` had no `TrustedUserCAKeys` or
`AuthorizedPrincipalsFile` directives, so sshd ignored those files and
rejected every cert with `Authentications that can continue: publickey,password`.

**Fix:** added to `alcatraz.conf`:
```
TrustedUserCAKeys /etc/ssh/trusted_user_ca_keys
AuthorizedPrincipalsFile /etc/ssh/auth_principals/%u
PasswordAuthentication no
```

### 4. Worker never tore down VMs on `vm.destroy`; API never observed `vm.destroyed`

Two halves of the same broken loop.

**Worker side** (`alcatraz.worker/cmd/alcatraz-worker/main.go`,
`internal/vm/machine.go`, `internal/messaging/config.go`):

The worker subscribed to `vm.spawn` only. `sandbox delete` published
`vm.destroy` on NATS, but nothing consumed it — VMs ran forever, the
post-exit cleanup goroutine that publishes `vm.destroyed` never fired.

**Fix:**
- Added `DestroySubject` (`vm.destroy`) and `DestroyQueueGroup`
  (`worker-vm-destroy`) to the worker's `messaging.Config`. The pre-existing
  spawn group `vm-workers` was renamed to `worker-vm-spawn` at the same time
  so all four queue groups share a `<consumer>-<subject>` convention with
  the API's `api-vm-ready` / `api-vm-destroyed`.
- Added `VirtualMachineService.Destroy(id)` — calls `StopVMM` on the matching
  Firecracker process, idempotent on unknown ids. The existing post-exit
  cleanup goroutine handles CNI DEL, slot release, and `vm.destroyed` publish.
- Added a second NATS subscriber in `main.go` that wires `vm.destroy` →
  `mgr.Destroy`.

**API side** (`alcatraz.api/src/Alcatraz.Infrastructure/Messaging/`,
`Application/Sandboxes/MarkSandboxDestroyed/`,
`Domain/Sandboxes/Sandbox.cs`):

The API only subscribed to `vm.ready`. Even if the worker had published
`vm.destroyed`, the DB stayed on `Running`/`Deleting` forever because nothing
listened.

**Fix:**
- New `Sandbox.MarkDestroyed(utcNow)` domain method. Maps
  `Provisioning|Running` → `Failed` (unexpected exit) and `Deleting` →
  `Deleted` (worker confirmed our destroy request). Already-terminal states
  (`Deleted`, `Failed`) are idempotent so at-least-once delivery doesn't
  thrash.
- New `MarkSandboxDestroyedCommand` + handler.
- New `VmDestroyedConsumer` — mirrors `VmReadyConsumer` shape, subscribes to
  `vm.destroyed` on queue group `api-vm-destroyed`.
- Registered as a hosted service in `DependencyInjection.cs`.
- Added `DestroyedSubject` / `DestroyedQueueGroup` to `NatsOptions` (defaults
  `vm.destroyed` / `api-vm-destroyed` — no config change needed).

### 5. `POST /users/register` returned 500 instead of reconciling Keycloak 409

**Files:** `Alcatraz.Infrastructure/Authentication/AuthenticationService.cs`,
`AdminAuthorizationDelegatingHandler.cs`, `Repositories/UserRepository.cs`,
`Domain/Users/IUserRepository.cs`,
`Application/Users/RegisterUser/RegisterUserCommandHandler.cs`.

Three layered problems:
1. The `AdminAuthorizationDelegatingHandler` called
   `EnsureSuccessStatusCode` on every Keycloak admin response, so the inner
   service never saw 409.
2. `AuthenticationService.RegisterAsync` then tried to read the `Location`
   header from the (already-thrown) response, which would have surfaced a
   confusing `"Location header can't be null"` even if the handler hadn't
   thrown first.
3. The orphan case (Keycloak has the user but the local `users` row is
   missing — e.g. DB volume wiped while Keycloak's wasn't) had no
   reconciliation path: re-register failed with 409, login failed because
   claims transformation hit `Sequence contains no elements` looking up
   roles by identity_id.

**Fix:**
- Removed `EnsureSuccessStatusCode` from the delegating handler — the only
  caller is `AuthenticationService`, which now does its own status checks.
- On Keycloak 409, `RegisterAsync` queries
  `GET /users?email=&exact=true` (with a minimal `(Id, Email)` projection
  to dodge the `access` field's bool-vs-string deserialization issue) and
  returns the existing `identityId`.
- Added `IUserRepository.GetByIdentityIdAsync`. The register handler looks
  up the local row by `identityId` after the IDP call; if found, returns
  the existing `user.Id` instead of inserting a duplicate. If not found
  (orphan case), a fresh local row is created with the existing
  `identityId`, restoring the user's ability to log in.

## Verification

End-to-end, against the rebuilt stack (`docker compose up -d --build
alcatraz.api`) with the worker running on the host (`sudo -E
./bin/alcatraz-worker`):

| Step | Result |
|---|---|
| `sandbox create` | status=1, worker logs `vm.spawn` |
| Wait for `vm.ready` | status=2, host=`172.16.0.X` (no `/24`) |
| `ssh-cert` + `ssh al@host` | shell as `al`, kernel `6.1.166`, principal file = sandbox UUID, CA pubkey present |
| `sandbox delete` | status=3, API publishes `vm.destroy` |
| Wait for `vm.destroyed` | status=4 within ~10s |
| `register demo@alcatraz.local` (existing) | HTTP 200, returns existing local `user.Id`, no 500 |
| `register new-user-XXXX@...` (fresh) | HTTP 200, fresh `user.Id` |

API startup logs now show both subscriptions:
```
Subscribing to NATS subject vm.ready (queue group api-vm-ready)
Subscribing to NATS subject vm.destroyed (queue group api-vm-destroyed)
```

## Files changed

```
alcatraz.api/src/Alcatraz.Api/appsettings.Development.json          (Issuer + Gateway, prior session)
alcatraz.api/src/Alcatraz.Application/Sandboxes/MarkSandboxDestroyed/MarkSandboxDestroyedCommand.cs        (new)
alcatraz.api/src/Alcatraz.Application/Sandboxes/MarkSandboxDestroyed/MarkSandboxDestroyedCommandHandler.cs (new)
alcatraz.api/src/Alcatraz.Application/Users/RegisterUser/RegisterUserCommandHandler.cs   (idempotent reconciliation)
alcatraz.api/src/Alcatraz.Domain/Sandboxes/Sandbox.cs                                    (MarkDestroyed)
alcatraz.api/src/Alcatraz.Domain/Users/IUserRepository.cs                                (GetByIdentityIdAsync)
alcatraz.api/src/Alcatraz.Infrastructure/Authentication/AdminAuthorizationDelegatingHandler.cs (drop EnsureSuccessStatusCode)
alcatraz.api/src/Alcatraz.Infrastructure/Authentication/AuthenticationService.cs         (409 reconciliation)
alcatraz.api/src/Alcatraz.Infrastructure/DependencyInjection.cs                          (register VmDestroyedConsumer)
alcatraz.api/src/Alcatraz.Infrastructure/Messaging/NatsOptions.cs                        (DestroyedSubject)
alcatraz.api/src/Alcatraz.Infrastructure/Messaging/VmDestroyedConsumer.cs                (new)
alcatraz.api/src/Alcatraz.Infrastructure/Repositories/UserRepository.cs                  (GetByIdentityIdAsync impl)
alcatraz.core/rootfs/init                                                                (exec sleep infinity, root-owned, applied via sudo)
alcatraz.core/rootfs/etc/ssh/sshd_config.d/alcatraz.conf                                 (TrustedUserCAKeys + AuthorizedPrincipalsFile, root-owned, applied via sudo)
alcatraz.worker/cmd/alcatraz-worker/main.go                                              (vm.destroy subscriber)
alcatraz.worker/internal/messaging/config.go                                             (DestroySubject + DestroyQueueGroup)
alcatraz.worker/internal/vm/machine.go                                                   (Destroy method)
alcatraz.worker/internal/vm/spawn.go                                                     (.IP.String() — prior session)
docker-compose.yml                                                                       (KC_HOSTNAME_BACKCHANNEL_DYNAMIC, prior session)
```

## Hardening pass (post-fix audit)

A second pass surfaced a handful of shortcuts taken in the heat of debug.
Each was either fixed properly or verified non-issue.

### `exec sleep infinity` → tini-managed sshd as PID 1

`/init` previously ended with `exec sleep infinity`, which kept the kernel
happy but didn't reap zombies and didn't forward signals — fine for a
test-cycle workaround, not for a customer-facing sandbox.

**Canonical fix** (in `alcatraz.core/build-rootfs.sh`):
- Added `tini` to `BASE_APT_PACKAGES`.
- Init now does mounts + host-key generation, then `exec /usr/bin/tini --
  /usr/sbin/sshd -D -o HostKey=...`. tini becomes PID 1 (signal forwarding
  + zombie reaping), sshd runs in the foreground, sshd's lifetime *is* the
  VM's lifetime — if it dies, tini exits with its code, kernel panics
  (`panic=1 reboot=k`), worker observes the exit and publishes
  `vm.destroyed`.
- Dropped the daemonized-sshd-then-block pattern entirely.

**Deployment note:** the live rootfs at `alcatraz.core/rootfs/` is still on
the `sleep infinity` test patch (auto mode blocked installing tini into the
live rootfs out-of-band, which is the right call for a path that ends up as
PID 1 in customer VMs). To redeploy:
- Preferred: rerun `build-rootfs.sh` (full debootstrap), or
- Faster: `sudo chroot alcatraz.core/rootfs apt-get update && apt-get install -y tini`
  followed by overwriting `alcatraz.core/rootfs/init` with the build script's
  template output.

### `UserRepresentationModel.Access` typed wrong

The model declared `Access` as `Dictionary<string, string>`, but Keycloak
returns `Dictionary<string, bool>` (`{"manageGroupMembership": true, ...}`).
This had been broken since day one — any code path that deserialized a
`UserRepresentationModel` from a Keycloak admin GET would have thrown.

**Fix** (`Authentication/Models/UserRepresentationModel.cs`): corrected
`Access` to `Dictionary<string, bool>` and `ClientRoles` to
`Dictionary<string, List<string>>` (matches Keycloak's actual representation
— client-id → list of role names). Dropped the `KeycloakUserLookup` minimal
projection from `AuthenticationService` that I had introduced as a workaround;
the lookup now uses the proper `UserRepresentationModel` directly.

### `AdminAuthorizationDelegatingHandler` contract documented

After dropping `EnsureSuccessStatusCode` from the handler so
`AuthenticationService` could see Keycloak 409, the handler had no machine-
or human-enforced contract that callers must check status themselves.

**Fix** (`Authentication/AdminAuthorizationDelegatingHandler.cs`): added an
XML doc comment on the class spelling out the responsibility split — the
handler attaches the bearer header and forwards the response untouched;
*callers* must `EnsureSuccessStatusCode()` / inspect status. Anyone adding
a new HTTP client wired to this handler will see the contract in IntelliSense.

### Outbox `IDomainEvent` deserialization

Verified: `TypeNameHandling.All` is set on both writer
(`ApplicationDbContext.JsonSerializerSettings`) and reader
(`ProcessOutboxMessagesJob.JsonSerializerSettings`). A freshly written
outbox row contains the discriminator:
```
{"$type": "Alcatraz.Domain.Sandboxes.Events.SandboxRequestedDomainEvent, ...
```
The earlier "Could not create an instance of IDomainEvent" was a stale row
from a prior dev cycle. **No code change needed.**

### `--json` flag on CLI `sandbox ssh-cert` (deferred)

Pre-existing CLI bug: `--json` doesn't suppress ANSI-colored human output.
Out of scope for this session; tracked as a known issue at the bottom.

---

## Gotchas introduced this session

### `alcatraz.core/rootfs/` is gitignored

Two of the five fixes were applied to files that **never get committed**:

```
alcatraz.core/rootfs/init                                  (root-owned, gitignored)
alcatraz.core/rootfs/etc/ssh/sshd_config.d/alcatraz.conf   (root-owned, gitignored)
```

Implications:
- **Fresh clone** gets the fix automatically — `build-rootfs.sh` was updated
  to template the corrected `/init` (with `exec sleep infinity`) and the
  correct `sshd_config.d/alcatraz.conf` (with `TrustedUserCAKeys` /
  `AuthorizedPrincipalsFile`). Running the script produces a working rootfs.
- **Existing rootfs from before today** does NOT get the fix unless either
  (a) you re-run `build-rootfs.sh` (heavy — re-debootstraps Ubuntu), or
  (b) you manually patch the two files (`sudo install -m 0755 -o root -g
  root /tmp/alcatraz-init.new …` and the equivalent for the sshd config —
  see "Bugs fixed" §2 / §3 above for the file contents).

The build script update is the canonical fix; the live patches were the
test-cycle workaround.

### `AdminAuthorizationDelegatingHandler` no longer throws on non-2xx

Previously, every Keycloak admin response went through
`EnsureSuccessStatusCode()` in the handler. That hid 409s from
`AuthenticationService` and made idempotent register impossible. Removed.

Verified there's only one client using this handler (`AuthenticationService`,
registered in `DependencyInjection.cs:144`), and that service now does its own
status checks. **Anyone adding a new `AddHttpMessageHandler<…>()` registration
must remember to check `IsSuccessStatusCode` themselves** — the handler no
longer does it for them.

### `AuthenticationService.RegisterAsync` lookup-by-email doesn't paginate

Keycloak's `GET /users?email=&exact=true` returns at most the realm's
`usersListPaginationLimit` (default 10) without `first`/`max` query params.
For an exact email lookup that's effectively always 1 result, but worth
knowing if anyone broadens the query later.

### `alcatraz.worker/.env` is tracked in git

Unusual (no `.env` entry in `.gitignore`), but apparently intentional — the
file holds deployable defaults, not secrets. The session's update to
`NATS_QUEUE_GROUP=worker-vm-spawn` etc. ships to everyone via this file.

## Files that need `git add` before committing

These were created during the session and aren't yet tracked. `git commit -a`
will skip them; explicitly `git add` first:

```
alcatraz.api/src/Alcatraz.Application/Sandboxes/MarkSandboxDestroyed/MarkSandboxDestroyedCommand.cs
alcatraz.api/src/Alcatraz.Application/Sandboxes/MarkSandboxDestroyed/MarkSandboxDestroyedCommandHandler.cs
alcatraz.api/src/Alcatraz.Infrastructure/Messaging/VmDestroyedConsumer.cs
plans/2026-05-09-end-to-end-debug.md
```

The `.csproj` files glob `*.cs` implicitly so MSBuild already includes the
new C# files (build is green). Git, however, only commits what it's told.

## Verified non-issues

These were checked and are fine — listed so a future reader doesn't repeat
the audit:

- **No DB migrations needed.** `MarkDestroyed` only adds enum-value
  transitions (`SandboxStatus.Failed = 5` and `Deleted = 4` were already in
  the enum). `GetByIdentityIdAsync` uses the existing `users.identity_id`
  column.
- **Both API consumers wire up correctly.** Startup logs show:
  ```
  Subscribing to NATS subject vm.ready (queue group api-vm-ready)
  Subscribing to NATS subject vm.destroyed (queue group api-vm-destroyed)
  ```
- **No new host-side dependencies.** `/run/alcatraz-ca/alcatraz_ca.pub` was
  already required by the worker before this session and is documented in
  `alcatraz.worker/README.md` and both `local-end-to-end.md` files.

## Known remaining issues

- An older outbox message contains an `IDomainEvent` payload that the JSON
  drainer can't deserialize (`Could not create an instance of type
  Alcatraz.Domain.Abstractions.IDomainEvent`). Cosmetic: the message will
  retry forever in the log. Fix is a `TypeNameHandling` / discriminator on
  the outbox serializer.
- A few orphaned veth interfaces remain on `alcatraz0` from VMs spawned by
  the previous worker generation before the destroy subscriber was added.
  Harmless. They go away on next `sudo systemctl restart` of the worker (or
  `sudo ip link delete vethXXXX` per interface) — the new worker will sweep
  IPAM but leftover taps in someone else's tracking map need manual cleanup.
- The CLI's `--json` flag on `sandbox ssh-cert` still emits ANSI-colored
  human output instead of raw JSON. Workaround: `sed 's/\x1b\[[0-9;]*m//g'`
  + awk on `Gateway:`. Fix is in the CLI's renderer (Spectre.Console
  formatter selection on `--json`).
