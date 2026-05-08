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

Each component owns one slice of the customer's path from `alcatraz login` to a shell inside a sandbox.

### `alcatraz.cli` — the customer's entry point *(shipped, [README](alcatraz.cli/README.md))*
- Logs the customer in via OAuth device flow (proxied by the API).
- Creates, lists, and deletes sandboxes.
- Fetches a short-lived SSH cert for a sandbox and opens a shell into it via stock `ssh`.
- Holds no server state and no long-lived secrets.

### `alcatraz.api` — the customer-facing control plane *(shipped, [README](alcatraz.api/README.md))*
- Owns customer identity, registration, and the role/permission model.
- Owns the `Sandbox` aggregate and its lifecycle (create, list, get, delete).
- Acts as the SSH certificate authority — issues short-lived user certs scoped to a single sandbox.
- Dispatches spawn and destroy work to workers.
- Never holds customer SSH keys, never runs VMs.

### `alcatraz.worker` — sandbox lifecycle on the host *(shipped, [README](alcatraz.worker/README.md))*
- Claims spawn and destroy jobs and runs them.
- Allocates host capacity (slots, IPs, TAPs) per sandbox.
- Sets up and tears down per-sandbox networking and outbound NAT.
- Provides each sandbox with a persistent, auditable filesystem overlay on top of the shared base image.
- Boots and supervises the sandbox VM; cleans up on exit.

### `alcatraz.core` — the sandbox itself *(shipped, [README](alcatraz.core/README.md))*
- The Linux environment customers actually SSH into.
- Owns the guest OS, the pre-installed developer tooling, and `sshd`.
- Owns the boot-from-overlay contract: a clean, reusable base image plus a per-sandbox delta that survives restarts.
- Trusts only certificates signed by the API's SSH CA.

### `alcatraz.gateway` — single SSH ingress *(planned)*
- Terminates TLS for incoming customer SSH connections from the public internet.
- Routes each connection to the correct sandbox VM's `sshd`, using the sandbox principal embedded in the cert.
- The only component reachable from outside the cluster on the SSH path.

## End-to-end request lifecycle

1. **Login.** CLI runs `alcatraz login`. The API initiates Keycloak's OAuth 2.0 device flow; the user signs in once in a browser. The CLI gets a JWT access token. The CLI never sees Keycloak's realm, client_id, or secret.
2. **Create sandbox.** CLI runs `alcatraz sandbox create`. The API persists a `Sandbox` row and writes a `SandboxRequested` outbox message in the same DB transaction.
3. **Spawn.** A Quartz job drains the outbox and publishes `vm.spawn` on NATS. A worker in the queue group claims the message, allocates a slot, prepares an AgentFS overlay, starts an in-process NFSv3 server, and boots Firecracker.
4. **Get a cert.** CLI runs `alcatraz connect <sandbox>`. The CLI generates a workstation ed25519 keypair locally, sends the public key to the API, and the API's SSH CA shells out to `ssh-keygen -s` to sign a 24-hour user cert with `principal = sandbox-UUID`.
5. **Connect.** The CLI invokes stock `ssh -i cert al@gateway`. The gateway terminates TLS and proxies bytes to the VM's `sshd`. `sshd` is configured with `TrustedUserCAKeys = api's CA pubkey` and `AuthorizedPrincipalsFile` listing the sandbox UUID, so the cert validates without any per-customer key ever touching the VM.
6. **Delete.** On `alcatraz sandbox delete`, the API marks the row `Deleting` and publishes `vm.destroy`. The worker tears down CNI, NFS, and Firecracker. Cert TTL expiry handles revocation in steady state; KRL is reserved for sub-TTL revocation later.

## Local development

A single root-level [`docker-compose.yml`](docker-compose.yml) brings up everything except the worker (which needs host KVM/CNI) and the CLI (built locally). The compose stack alone is enough for an end-to-end test of the customer flow — register → login → create sandbox → fetch SSH cert → SSH into a sandbox stand-in — without the worker or the gateway.

### Prerequisites

- Docker Compose
- `curl`, `jq`, `ssh-keygen`, `ssh` on the host
- A web browser (only if you take the real device-flow path in step 2)
- .NET 8 SDK (only if you want to run the CLI or the API on the host)

### Bring up the stack

```bash
# from the repo root
docker compose up -d --build
docker compose logs -f forqstudio.api    # wait for "Now listening on: http://[::]:8080"
```

The first build is slow (multi-stage .NET image). Subsequent runs are fast — drop `--build` unless you've edited `alcatraz.api/src/`, `alcatraz.api/.files/ca-init/`, or `alcatraz.api/.files/demo-sshd/`.

What's running once it's up:

| Service | Port | Purpose |
|---|---|---|
| `forqstudio.api` | `:8080` | Control plane API |
| `forqstudio-idp` | `:8082` | Keycloak with the `forqstudio` realm pre-imported (device flow enabled) |
| `forqstudio-db` | `:5432` | Postgres |
| `forqstudio-redis` | `:6379` | Redis |
| `forqstudio-nats` | `:4222` (mon `:8222`) | NATS broker |
| `forqstudio-seq` | `:8083` | Seq log viewer |
| `forqstudio-ca-init` | — | One-shot: writes the SSH CA key into the shared `alcatraz_ca` volume |
| `forqstudio-demo-sshd` | `:2222` | Alpine `sshd` standing in for a Firecracker VM (mirrors the rootfs convention) |

`forqstudio-demo-sshd` exists so the cert pipeline is testable without the worker. It mounts the same CA pubkey, runs `sshd` with `TrustedUserCAKeys` and `AuthorizedPrincipalsFile`, and is what `alcatraz.core` looks like in production minus Firecracker.

### End-to-end walkthrough

This uses `curl` and stock `ssh` so each step is verifiable without CLI code. The CLI path is the same flow with friendlier UX — see [`alcatraz.cli/README.md`](alcatraz.cli/README.md).

#### 1. Register a user

The realm ships empty of human users. Registering through the API creates the Keycloak account and the local `users` row in one transaction.

```bash
curl -sX POST http://localhost:8080/api/v1/users/register \
  -H 'content-type: application/json' \
  -d '{"email":"demo@alcatraz.local","firstName":"Demo","lastName":"User","password":"demopass"}'
```

#### 2. Get an access token

**Fast path** (password grant, no browser — useful for scripting):

```bash
TOKEN=$(curl -sX POST http://localhost:8080/api/v1/users/login \
  -H 'content-type: application/json' \
  -d '{"email":"demo@alcatraz.local","password":"demopass"}' | jq -r .accessToken)
```

**Real device-flow path** (what the CLI does):

```bash
INIT=$(curl -sX POST http://localhost:8080/api/v1/auth/device)
DEVICE_CODE=$(echo "$INIT" | jq -r .deviceCode)
echo "Open: $(echo "$INIT" | jq -r .verificationUriComplete)"
read -p "Press Enter once you've signed in..."

TOKEN=$(curl -sX POST http://localhost:8080/api/v1/auth/device/token \
  -H 'content-type: application/json' \
  -d "{\"deviceCode\":\"$DEVICE_CODE\"}" | jq -r .accessToken)
```

While polling, `POST /auth/device/token` returns HTTP 400 with `"error": "authorization_pending"` until the browser sign-in completes — that's the RFC 8628 idiom the CLI implements.

#### 3. Create a sandbox

```bash
SANDBOX=$(curl -sX POST http://localhost:8080/api/v1/sandboxes \
  -H "authorization: bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"vcpus":2,"memoryMib":2048}')
ID=$(echo "$SANDBOX" | jq -r .id)
```

The API persists the row, writes a `SandboxRequested` outbox message in the same transaction, and a Quartz job publishes `vm.spawn` on NATS. With no worker running, the sandbox stays in `Provisioning` — expected. To watch the message land:

```bash
docker run --rm --network alcatraz_default natsio/nats-box:latest \
  nats sub vm.spawn -s nats://forqstudio-nats:4222
```

#### 4. Stand in for the worker

The worker would normally write the sandbox UUID into `/etc/ssh/auth_principals/al` in the AgentFS overlay before booting the VM. Replicate that on the demo container:

```bash
docker exec ForqStudio.DemoSshd sh -c "echo $ID > /etc/ssh/auth_principals/al"
```

#### 5. Fetch an SSH cert

```bash
ssh-keygen -t ed25519 -f /tmp/id_alcatraz -N "" -C "demo@workstation"
PUB=$(cat /tmp/id_alcatraz.pub)

curl -sX POST "http://localhost:8080/api/v1/sandboxes/$ID/ssh-cert" \
  -H "authorization: bearer $TOKEN" -H 'content-type: application/json' \
  -d "{\"sshPublicKey\":\"$PUB\"}" | jq -r .cert > /tmp/id_alcatraz-cert.pub

ssh-keygen -L -f /tmp/id_alcatraz-cert.pub
# Type: user cert | Principal: <ID> | Valid: now → +24h | Signing CA: alcatraz-demo-ca
```

#### 6. SSH into the sandbox

```bash
ssh -i /tmp/id_alcatraz \
    -i /tmp/id_alcatraz-cert.pub \
    -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
    -p 2222 al@localhost
```

You should land in a shell as `al`; `sudo whoami` returns `root`. Stock `ssh` + stock `sshd` accepted a cert minted by the API's CA, scoped to a specific sandbox UUID, with no shared keys ever changing hands. That's the whole proof.

### Resetting the stack

```bash
docker compose down -v   # drops keycloak_data and alcatraz_ca volumes
docker compose up -d --build
```

You **must** wipe volumes if you change the realm export — Keycloak only imports on first boot. Wiping `alcatraz_ca` regenerates the CA key, which invalidates every previously issued cert.

### Running the CLI or worker

- **CLI:** `dotnet run --project alcatraz.cli/src/Alcatraz.Cli -- login`. Talks to `http://localhost:8080` by default and runs the same device-flow + sandbox CRUD walkthrough above with friendlier UX.
- **Worker:** `cd alcatraz.worker && make build && sudo -E ./bin/alcatraz-worker`. Host-run because it needs KVM and CNI plugins; not in compose.

### Per-component notes

- [`alcatraz.api/README.md`](alcatraz.api/README.md) — control plane (the `forqstudio.api` service in compose).
- [`alcatraz.worker/README.md`](alcatraz.worker/README.md) — NATS subscriber + Firecracker, host-run (requires KVM and CNI plugins).
- [`alcatraz.core/README.md`](alcatraz.core/README.md) — kernel and Ubuntu rootfs build (requires `sudo`, `debootstrap`, and the host `agentfs` binary).
- [`alcatraz.cli/README.md`](alcatraz.cli/README.md) — customer CLI.
- [`alcatraz.api/docs/local-end-to-end.md`](alcatraz.api/docs/local-end-to-end.md) — deeper notes: negative test cases, what the demo doesn't prove yet.

## Project layout

```
alcatraz/
├── docker-compose.yml # single source of truth for local dev (API + IdP + db + cache + msgq + demo sshd)
├── alcatraz.api/      # .NET 8 control plane: auth proxy, sandbox aggregate, SSH CA
├── alcatraz.cli/      # .NET 8 customer CLI: device-flow login, sandbox CRUD, SSH cert fetch + ssh wrapper
├── alcatraz.core/     # Firecracker kernel/rootfs build scripts and launchers
├── alcatraz.worker/   # Go worker: NATS sub + CNI + in-process AgentFS/NFS + Firecracker SDK (host-run, not in compose)
└── plans/             # cross-component design docs
```

## Design references

- [`plans/customer-vm-access-ssh-ca.md`](plans/customer-vm-access-ssh-ca.md) — system-of-record for SSH CA, device flow, and gateway architecture.
- [`plans/alcatraz-api-cli-endpoints.md`](plans/alcatraz-api-cli-endpoints.md) — control-plane endpoint spec.
- [`plans/alcatraz-cli.md`](plans/alcatraz-cli.md) — CLI implementation plan, including the API refresh-token endpoint and the docker-compose consolidation.
