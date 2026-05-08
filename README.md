# Alcatraz

A sandboxed environment for letting your coding agents run wild.

Customers spin up isolated, ephemeral Linux sandboxes from a CLI, get a short-lived SSH cert, and connect to a Firecracker microVM whose root filesystem is an audited AgentFS overlay. Base image stays clean; per-sandbox changes persist into a SQLite delta you can diff and replay.

## What you get

- **Per-customer Firecracker microVMs** spawned on demand (KVM-backed, sub-second boot, hard isolation).
- **Stock-`ssh` access via short-lived OpenSSH user certificates** — no shared keys, no pubkey storage, expiry-driven revocation.
- **Audited filesystem changes via AgentFS overlay on NFS** — overlay persists across restarts, base image is reusable.
- **Multi-host NATS-driven worker pool** with a Keycloak-backed control plane.

## Architecture

```
     ┌───────────────────────┐
     │      alcatraz.cli     │  customer's terminal
     └──────────┬────────────┘
                │ HTTPS
                ▼
     ┌───────────────────────┐         ┌──────────────────┐
     │     alcatraz.api      │ ──→ AuthN ──→ │ Keycloak (IdP)   │
     │  (control plane)      │         └──────────────────┘
     │  • device flow proxy  │
     │  • Sandbox aggregate  │ ──→ NATS  vm.spawn / vm.destroy
     │  • SSH CA             │                  │
     └───────────┬───────────┘                  ▼
                 │                  ┌──────────────────┐
                 │ short-lived      │  alcatraz.worker │
                 │ ssh user cert    │  • NATS sub      │
                 │                  │  • CNI bridge    │
                 │                  │  • AgentFS+NFS   │
                 │                  └────────┬─────────┘
                 │                           │ spawns
                 │                           ▼
                 │                  ┌──────────────────┐
                 │                  │  alcatraz.core   │
                 │                  │  (Firecracker VM)│
                 │                  │  Ubuntu + sshd   │
                 │                  └────────┬─────────┘
                 │                           │
                 └───── ssh ────► alcatraz.gateway ────► sshd in VM
                       (TLS)      (planned)              (TrustedUserCAKeys
                                                          = alcatraz.api's
                                                            CA pubkey)
```

## Components

| Component | Status | Description | Link |
|---|---|---|---|
| `alcatraz.core` | shipped | Firecracker microVM with AgentFS overlay-backed NFS root | [README](alcatraz.core/README.md) |
| `alcatraz.worker` | shipped | NATS-driven Go worker that spawns and tears down VMs | [README](alcatraz.worker/README.md) |
| `alcatraz.api` | shipped | .NET 8 control plane — device-flow auth proxy, sandbox CRUD, SSH CA | [README](alcatraz.api/README.md) |
| `alcatraz.gateway` | planned | TLS-terminating SSH ingress in front of worker VMs | — |
| `alcatraz.cli` | planned | Customer CLI — wraps device-flow login + sandbox commands + cert fetch | — |

## End-to-end request lifecycle

1. **Login.** CLI runs `alcatraz login`. The API initiates Keycloak's OAuth 2.0 device flow; the user signs in once in a browser. The CLI gets a JWT access token. The CLI never sees Keycloak's realm, client_id, or secret.
2. **Create sandbox.** CLI runs `alcatraz sandbox create`. The API persists a `Sandbox` row and writes a `SandboxRequested` outbox message in the same DB transaction.
3. **Spawn.** A Quartz job drains the outbox and publishes `vm.spawn` on NATS. A worker in the queue group claims the message, allocates a slot, prepares an AgentFS overlay, starts an in-process NFSv3 server, and boots Firecracker.
4. **Get a cert.** CLI runs `alcatraz connect <sandbox>`. The CLI generates a workstation ed25519 keypair locally, sends the public key to the API, and the API's SSH CA shells out to `ssh-keygen -s` to sign a 24-hour user cert with `principal = sandbox-UUID`.
5. **Connect.** The CLI invokes stock `ssh -i cert al@gateway`. The gateway terminates TLS and proxies bytes to the VM's `sshd`. `sshd` is configured with `TrustedUserCAKeys = api's CA pubkey` and `AuthorizedPrincipalsFile` listing the sandbox UUID, so the cert validates without any per-customer key ever touching the VM.
6. **Delete.** On `alcatraz sandbox delete`, the API marks the row `Deleting` and publishes `vm.destroy`. The worker tears down CNI, NFS, and Firecracker. Cert TTL expiry handles revocation in steady state; KRL is reserved for sub-TTL revocation later.

## Local development

Each component has its own `docker compose` / `make` workflow. Start with the component you're working on:

- [`alcatraz.api/README.md`](alcatraz.api/README.md) — control plane plus Keycloak, Postgres, Redis, NATS, Seq, and a stand-in `sshd` container.
- [`alcatraz.worker/README.md`](alcatraz.worker/README.md) — NATS subscriber + Firecracker (requires KVM and CNI plugins on the host).
- [`alcatraz.core/README.md`](alcatraz.core/README.md) — kernel and Ubuntu rootfs build (requires `sudo`, `debootstrap`, and the host `agentfs` binary).

End-to-end demo without writing the CLI: see [`alcatraz.api/docs/local-end-to-end.md`](alcatraz.api/docs/local-end-to-end.md). It covers the full register → device login → create sandbox → fetch cert → SSH walkthrough using only `curl` and stock `ssh`, against the `alcatraz.api` compose stack.

## Project layout

```
alcatraz/
├── alcatraz.api/      # .NET 8 control plane: auth proxy, sandbox aggregate, SSH CA
├── alcatraz.core/     # Firecracker kernel/rootfs build scripts and launchers
├── alcatraz.worker/   # Go worker: NATS sub + CNI + in-process AgentFS/NFS + Firecracker SDK
└── plans/             # cross-component design docs
```

## Design references

- [`plans/customer-vm-access-ssh-ca.md`](plans/customer-vm-access-ssh-ca.md) — system-of-record for SSH CA, device flow, and gateway architecture.
- [`plans/alcatraz-api-cli-endpoints.md`](plans/alcatraz-api-cli-endpoints.md) — control-plane endpoint spec.
