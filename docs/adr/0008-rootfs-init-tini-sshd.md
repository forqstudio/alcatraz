# ADR-0008: Rootfs init = `tini → sshd`; sshd's lifetime *is* the VM's lifetime

- **Status:** Accepted
- **Date:** 2026-05-10
- **Related:** [`0004-customer-ssh-access.md`](0004-customer-ssh-access.md), [`0007-agentfs-overlay-pre-boot.md`](0007-agentfs-overlay-pre-boot.md)

## Context

The customer's sole reason for the VM existing is sshd. A VM where the kernel is healthy but sshd has died is the worst kind of failure: the readiness probe is TCP-on-22, but if `init` keeps running after sshd exits, the worker observes "VM still alive" while the customer sees connection-refused. That gap — *running but broken* — produces stuck Provisioning, mysterious "I can't ssh in," and a debugging loop where the operator has to introspect the VM to discover that the only thing the VM was for is gone.

Two design choices interact:

1. **Init choice.** A traditional init (`systemd`, `openrc`, `sysvinit`) keeps the VM alive after sshd exits. That's the wrong default for a single-purpose VM.
2. **Failure semantics.** When sshd dies, the system should fail loudly and visibly so the worker can clean up and the API can transition the sandbox to `Failed`.

## Decision

Use **`tini` as PID 1, exec sshd directly under it, and tie sshd's lifetime to the VM's lifetime**. The kernel is configured to panic on init exit (`panic=1 reboot=k`).

Implementation:

- `alcatraz.core/build-rootfs.sh` installs `tini` into the rootfs.
- `/init` is templated to do the necessary mounts + ephemeral host-key generation, then `exec /usr/bin/tini -- /usr/sbin/sshd -D -o HostKey=...`.
- `tini` reaps zombies and forwards signals.
- `sshd_config.d/alcatraz.conf` ships with `TrustedUserCAKeys`, `AuthorizedPrincipalsFile /etc/ssh/auth_principals/%u`, `PasswordAuthentication no`.
- The live `alcatraz.core/rootfs/` tree is gitignored — `build-rootfs.sh` is the source of truth; a fresh clone gets the correct rootfs by running it.

Failure path: sshd exits → `tini` exits → kernel panics → Firecracker process exits → worker's `m.Wait` returns → worker publishes `vm.destroyed` → API state machine transitions `Provisioning|Running → Failed`.

## Consequences

### Positive

- **No "running but broken" state.** The kernel and sshd are bound; either both work or the VM is gone.
- **Loud failure.** A sshd crash produces a `vm.destroyed` event the API can act on, not a silent half-up VM.
- **Minimal init surface.** No service supervisor, no socket activation, no unit files. One process tree, one purpose.
- **Boot is fast.** `tini → sshd` is microseconds; almost all observed boot time is kernel + agentfs prep, not userspace init.
- **Source-of-truth-by-script.** The gitignored `rootfs/` tree means there is no drift between the build script and the artefact — anyone reproducing the build runs the same script.

### Negative

- **No general-purpose userspace.** A customer can't `systemctl start nginx`. That's intentional — this is a sandbox for SSH-driven work — but it does mean any future "and also expose a web service" requirement needs a different init story or a different VM type.
- **A sshd config bug bricks the VM.** Since sshd's exit panics the kernel, a typo in `sshd_config.d/alcatraz.conf` produces a tight crash loop visible only in worker logs. Mitigation is rootfs CI: a smoke test that boots the rootfs and probes sshd before promoting an image.
- **Host-key regenerates per boot.** Customers see a new host key every time their sandbox is spawned. Acceptable because sandboxes are throwaway and the cert is over the connection, not the host key — but would surprise someone expecting persistent identity.
