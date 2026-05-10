# ADR-0007: AgentFS overlay writes happen pre-boot

- **Status:** Accepted
- **Date:** 2026-05-10
- **Related:** [`0004-customer-ssh-access.md`](0004-customer-ssh-access.md), [`0008-rootfs-init-tini-sshd.md`](0008-rootfs-init-tini-sshd.md)

## Context

The customer-facing VM has **no out-of-band provisioning channel**: there is no agent we control inside the VM that we can reach over the network to push files post-boot, because the only network listener inside the VM is sshd, and sshd needs the per-VM principal scope and the trusted CA pubkey *to be in place before it accepts its first connection*.

The two files that have to land before sshd starts:

- `/etc/ssh/auth_principals/al` — single line containing the sandbox UUID. Without this, sshd accepts no certs (or worse, accepts certs scoped to other sandboxes).
- `/etc/ssh/trusted_user_ca_keys` — the alcatraz CA public key. Without this, sshd accepts no certs at all.

Constraints 1, 2, and 6 from the architecture overview all depend on those two files being correct **before** sshd answers. A half-trusted VM that reaches `Running` is the worst possible failure: it would pass the readiness probe, get a route in the gateway, accept a connection, and then either reject all certs (customer pain) or accept the wrong principal scope (tenant-isolation hole).

## Decision

The worker writes both files into the AgentFS overlay **between `agentfs.PrepareOverlay` and `m.Start(ctx)`** — i.e. after the overlay exists, before Firecracker boots the kernel. Specifically:

- `/etc/ssh/auth_principals/al` ← `<sandbox-id>\n` (mode 0644)
- `/etc/ssh/trusted_user_ca_keys` ← contents of `WORKER_CA_PUBKEY_PATH` (default `/run/alcatraz-ca/alcatraz_ca.pub`)

Both writes happen before sshd starts. **Failure aborts the spawn**; the overlay DB is wiped on the failure path so partial writes vanish. The sandbox transitions to `Failed`, never `Running`.

## Consequences

### Positive

- **Per-VM principal scope is a hard precondition for sshd answering**, not a runtime hope. There is no window in which sshd is up but the principals file is missing or stale.
- **The CA pubkey is identical to the one baked into the rootfs** (`alcatraz.core/build-rootfs.sh`), so an overlay-write failure or a rootfs CA mismatch is visible in the same place — sshd rejects the cert.
- **Atomicity with the spawn.** "Spawn succeeded" implies "trust files are in place." Operators and tests both rely on that invariant.
- **No agent-in-the-VM.** Constraint 6 is satisfied: the VM has no privileged surface area for the worker to reach back into post-boot.

### Negative

- **The worker holds the CA pubkey on disk.** `WORKER_CA_PUBKEY_PATH` defaults to `/run/alcatraz-ca/alcatraz_ca.pub` (tmpfs). If the host reboots and the sync script (`alcatraz.worker/scripts/sync-ca-pubkey.sh`) hasn't run, the worker fails fast on first spawn rather than booting half-trusted VMs — but that means a deploy-time setup step.
- **Pre-boot writes mean the worker has filesystem access to the overlay.** A worker compromise yields the ability to plant arbitrary auth state into the next spawned VM. The defence is keeping the worker's privilege envelope tight, not moving the writes.
- **No way to rotate per-VM auth files post-boot** without destroying the VM. Acceptable today because cert TTL is 24h and sandboxes are throwaway; would matter if we ever needed long-lived sandboxes.

### Locked-in invariants

- The overlay must exist before the writes; the writes must complete before `m.Start(ctx)`.
- A failure on either write aborts the spawn — no "boot anyway, retry later." Half-trusted VMs are worse than failed spawns.
