# Alcatraz.Worker

The `alcatraz-worker` is a Go application that listens to NATS messages to spawn Firecracker VMs dynamically. It supports multiple concurrent VMs, each with CNI-based networking via the Firecracker Go SDK and a shared bridge (`alcatraz0`).

This repository contains the build and launch scripts only. Generated artifacts such as `bin/`, `alcatraz.core/` symlinks, and log files are intentionally excluded from version control.

## The Stack

- Go: `1.25.0`
- Docker: NATS and Seq are provided by the repo-root `docker-compose.yml` (run alongside `alcatraz.api`); the worker itself runs on the host
- NATS: `2.10` (with JetStream)
- Seq: `2024.3` (structured log viewer; receives CLEF events from the worker)
- Firecracker target: `v1.15.1`
- AgentFS: in-process via the Go SDK (`github.com/tursodatabase/agentfs/sdk/go`); no `agentfs` CLI binary required. The SDK is resolved via a local-path `replace` directive in `go.mod` pointing at a sibling checkout of `github.com/tursodatabase/agentfs`.
- NFSv3 server: in-process via `github.com/willscott/go-nfs`
- Logging: `log/slog` (stdlib) fanned out to stdout + Seq via a small CLEF HTTP handler in `internal/logging`

## How It Works

1. Worker subscribes to NATS `vm.spawn` (queue group `worker-vm-spawn`, for load balancing) and `vm.destroy` (queue group `worker-vm-destroy`). Queue groups follow the `<consumer>-<subject>` convention also used by the API (`api-vm-ready`, `api-vm-destroyed`).
2. On VM request, worker allocates an available slot
3. Worker configures `CNIConfiguration` for the Firecracker SDK; on VM start, the SDK invokes CNI plugins (bridge, host-local IPAM, tc-redirect-tap) which handle TAP creation, IP allocation, and NAT automatically
4. Worker initializes or reuses an AgentFS overlay in `.agentfs/<agent-id>.db` via the AgentFS Go SDK (no subprocess)
5. Worker appends two SSH cert-auth args to the Firecracker kernel cmdline:
   - `alcatraz.agent_id=<sandbox-uuid>` ← the cert principal sshd must accept for this VM
   - `alcatraz.ca_pubkey=<base64>` ← contents of `WORKER_CA_PUBKEY_PATH` (the API's SSH CA pubkey), read once at worker startup

   The guest's `/init` parses both args from `/proc/cmdline` and materialises them in tmpfs at `/run/ssh-config/{auth_principals/al,trusted_user_ca_keys}` before sshd starts. No pre-boot overlay writes happen on the worker host side.
6. Worker starts an in-process NFSv3 server bound to the bridge gateway IP, exporting the overlay (host rootfs as read-only base layer + per-VM SQLite delta)
7. Worker spawns Firecracker VM with `root=/dev/nfs` pointing to that NFS export
8. After Firecracker reports started, worker probes `vm_ip:22` until sshd accepts (10s timeout) and publishes `vm.ready` on NATS with `{id, host, port}`. `alcatraz.api` consumes this event and transitions the sandbox to `Running`; `alcatraz.routes` (when present) updates Traefik's route table.
9. On `vm.destroy`, worker calls `StopVMM` on the matching Firecracker process. Firecracker forwards the signal to the guest's PID 1 (`tini`, see [`alcatraz.core/README.md`](../alcatraz.core/README.md)) which propagates `SIGTERM` to `sshd` for a clean shutdown. On VM exit (whether triggered by a `vm.destroy` request or an unexpected crash), the SDK's `doCleanup()` invokes CNI DEL to release TAP, IP, and namespace; worker shuts down the NFS server goroutine, releases the slot, and publishes `vm.destroyed`. `alcatraz.api` consumes that and transitions the sandbox to `Deleted` (if it was `Deleting`) or `Failed` (if it was `Running` / `Provisioning`); `alcatraz.routes` (when present) drops the route.

The practical effect is:
- VMs can be spawned on-demand via NATS messages
- Multiple workers can handle load balancing via queue groups
- Each VM gets a unique IP on a shared bridge with NFS root via AgentFS
- CNI plugins handle all networking setup and teardown automatically
- The worker is decoupled from `alcatraz.api`: they only share NATS subjects (`vm.spawn` / `vm.destroy` inbound, `vm.ready` / `vm.destroyed` outbound).
- **Note:** VMs on the same worker can currently communicate with each other via the shared bridge (see [docs/network-isolation.md](docs/network-isolation.md))

## Host Requirements

You need these on the host:
- Ubuntu or another Linux host with `sudo`
- KVM / Firecracker support
- `firecracker` binary available (auto-resolved to v1.15.1 or PATH)
- NATS server running
- CNI plugins installed at `/opt/cni/bin` (bridge, host-local, tc-redirect-tap)
- CNI conflist installed at `/etc/cni/net.d/alcatraz-bridge.conflist`
- The API's SSH CA pubkey readable at `WORKER_CA_PUBKEY_PATH` (default `/run/alcatraz-ca/alcatraz_ca.pub`). With the repo-root compose stack up, run:
  ```bash
  alcatraz.worker/scripts/sync-ca-pubkey.sh
  ```
  The script copies the pubkey out of the `alcatraz_ca` docker volume. `/run` is tmpfs, so re-run this after every host reboot. The worker fails fast at startup if the file isn't readable.

The AgentFS overlay and NFS server run in-process inside the worker — no separate `agentfs` daemon or CLI binary needs to be installed.

The worker requires `sudo` for CNI networking operations.

### CNI Prerequisites

Install CNI plugins:

```bash
# Standard CNI plugins (bridge, host-local, etc.)
curl -sL https://github.com/containernetworking/plugins/releases/download/v1.0.1/cni-plugins-linux-amd64-v1.0.1.tgz | \
  sudo tar -xz -C /opt/cni/bin/

# tc-redirect-tap (Firecracker-specific)
git clone https://github.com/firecracker-microvm/tc-redirect-tap.git
cd tc-redirect-tap && make && sudo cp tc-redirect-tap /opt/cni/bin/
```

Install the CNI config:

```bash
sudo mkdir -p /etc/cni/net.d
sudo cp cni/alcatraz-bridge.conflist /etc/cni/net.d/
```

The worker expects:
- Host uplink interface detectable from `ip route show default`
- `alcatraz.core/` with built kernel and rootfs available — by default at `<repo>/alcatraz.core/`, anchored to the worker binary's location (override per-path with `WORKER_FIRECRACKER_BIN`, `WORKER_KERNEL_PATH`, `WORKER_ROOTFS_PATH`, `WORKER_AGENTFS_DATA`):
  - `bin/firecracker-v1.15.1` - firecracker binary
  - `linux-amazon/vmlinux` - built kernel
  - `rootfs/` - built Ubuntu rootfs
  - `.agentfs/` - AgentFS overlay directory (created at runtime)

## Worker/VM Boundary

Worker-side:
- NATS subscriber (vm.spawn listener)
- VM service with slot allocation
- CNI-based networking (via Firecracker SDK CNIConfiguration)
- SDK-managed cleanup (CNI DEL on VM exit)
- AgentFS overlay initialization (in-process, AgentFS Go SDK)
- In-process NFSv3 server (`willscott/go-nfs` over a billy adapter that wraps the AgentFS overlay)
- Firecracker VM lifecycle

VM-side (delegated to alcatraz.core):
- Ubuntu userspace
- developer tools
- `/workspace`
- `sshd`
- outbound access through host NAT

## VM Defaults

- default vCPUs: `4`
- default Memory: `8192` MiB
- default kernel args: `loglevel=7 printk.devkmsg=on`
- hostname: `alcatraz`

All VMs share subnet `172.16.0.0/24` on bridge `alcatraz0`. IPs are allocated dynamically by the CNI host-local IPAM plugin from `172.16.0.10-250`:

| Slot | TAP Device | VM IP (dynamic)* | Gateway    | NFS Port |
|------|-----------|-------------------|------------|----------|
| 0    | fc-tap0   | 172.16.0.10       | 172.16.0.1 | 8000     |
| 1    | fc-tap1   | 172.16.0.11       | 172.16.0.1 | 8001     |
| 2    | fc-tap2   | 172.16.0.12       | 172.16.0.1 | 8002     |
| ...  | ...       | ...               | 172.16.0.1 | ...      |

\* VM IPs depend on allocation order, not slot index. Check worker logs for the actual assigned IP.

## Build

```bash
cd alcatraz.worker
make build
```

This produces `bin/alcatraz-worker` and `bin/spawn-client`.

## Run

NATS and Seq are part of the repo-root compose stack — bring that up first (it also starts `alcatraz.api`, Keycloak, Postgres, Redis, the SSH CA init, and the demo sshd):

```bash
cd ..   # to repo root
docker compose up -d
```

Seq UI: http://localhost:8083 (auth disabled for local dev). The worker ships CLEF events to `localhost:5341`. NATS is on `localhost:4222`. The default `alcatraz.worker/.env` already targets these endpoints.

Start the worker on the host (must run as root):

```bash
sudo -E ./alcatraz.worker/bin/alcatraz-worker     # from repo root
# or, equivalently
cd alcatraz.worker && sudo -E ./bin/alcatraz-worker
```

CWD doesn't matter. Path defaults are anchored to the executable's location (`os.Executable()` → `<repo>/alcatraz.worker/bin/alcatraz-worker` → `<repo>/alcatraz.core/...`), the `.env` next to the worker module is loaded the same way, and `vm.LoadConfig().ValidateArtifacts()` lstats firecracker / kernel / rootfs at startup. A missing artifact crashes on boot with a message naming the resolved path and the env var that overrides it — never the old failure mode where the worker accepted a spawn and only failed inside `prepare agentfs` 30 s later.

The worker is intentionally not part of `docker compose` — it needs KVM, CNI, and root-level network setup on the host.

## Configuration

The worker is configured entirely via environment variables. `.env` is loaded
from the directory containing the worker module (resolved from
`os.Executable()`, not from CWD), so the same `.env` is picked up whether you
launch from the repo root or from `alcatraz.worker/`. A missing `.env` is not
an error.

| Var | Default | Notes |
|---|---|---|
| `NATS_URL` | `nats://localhost:4222` | NATS server URL |
| `NATS_SUBJECT` | `vm.spawn` | Subject the worker subscribes to |
| `NATS_QUEUE_GROUP` | `worker-vm-spawn` | NATS queue group for `vm.spawn` consumers (load balancing across workers) |
| `NATS_READY_SUBJECT` | `vm.ready` | Subject the worker publishes to after sshd is reachable |
| `NATS_DESTROYED_SUBJECT` | `vm.destroyed` | Subject the worker publishes to after VM exit + cleanup |
| `NATS_DESTROY_SUBJECT` | `vm.destroy` | Subject the worker subscribes to for delete requests from the API |
| `NATS_DESTROY_QUEUE_GROUP` | `worker-vm-destroy` | NATS queue group for `vm.destroy` consumers |
| `WORKER_CA_PUBKEY_PATH` | `/run/alcatraz-ca/alcatraz_ca.pub` | API's SSH CA pubkey; read once at worker startup and embedded (base64) in each VM's kernel cmdline |
| `WORKER_FIRECRACKER_BIN` | `<repo>/alcatraz.core/bin/firecracker-v1.15.1` | Firecracker binary. Default derived from `os.Executable()` (binary at `<repo>/alcatraz.worker/bin/alcatraz-worker` → core dir is the sibling of `alcatraz.worker/`). Override for non-standard layouts. |
| `WORKER_KERNEL_PATH` | `<repo>/alcatraz.core/linux-amazon/vmlinux` | Guest kernel image. Same anchoring as above. |
| `WORKER_ROOTFS_PATH` | `<repo>/alcatraz.core/rootfs` | Guest rootfs base image (read-only; per-VM overlay is layered on top). |
| `WORKER_AGENTFS_DATA` | `<repo>/alcatraz.core/.agentfs` | Per-sandbox AgentFS overlay store. Created on demand if missing. |
| `SEQ_URL` | _(empty)_ | If set, ship CLEF events to Seq at this URL |
| `SEQ_API_KEY` | _(empty)_ | Optional Seq API key |
| `APPLICATION` | `alcatraz-worker` | Tag attached to every log event |
| `ENVIRONMENT` | `development` | Tag attached to every log event |

The four `WORKER_*` path overrides are validated at startup via `os.Stat` — a
missing or mistyped path crashes the process before it subscribes to NATS,
naming both the resolved path and the env var that would override it.

## Spawn a VM

Using spawn-client:

```bash
./bin/spawn-client -vcpus 2 -mem 2048
./bin/spawn-client -id my-vm -vcpus 4
```

Or using nats CLI:

```bash
nats pub vm.spawn '{"vcpus": 2, "memory_mib": 2048}' --creds=none -
```

### VM Request Schema

```json
{
  "id": "optional-vm-id",
  "vcpus": 4,
  "memory_mib": 8192,
  "kernel_args": "loglevel=7 printk.devkmsg=on"
}
```

All fields are optional. Defaults: vcpus=4, memory_mib=8192, kernel_args="loglevel=7 printk.devkmsg=on"

## Connect to VM

The customer-facing path is `alcatraz ssh <sandbox-id>` from `alcatraz.cli`, which polls the API for `Running` state, fetches a cert, and execs `ssh` against either Traefik (production) or the worker-reported VM IP (local dev).

For low-level debugging from the worker host, you can read the VM IP from the spawn logs (`"VM ready" vm_ip=172.16.0.10 …`) and connect with a cert you minted yourself via the API. Password auth is disabled in the rootfs sshd config — the cert chain is the only auth path.

## Persistence Model

- Base image: `alcatraz.core/rootfs`
- Overlay DB: `alcatraz.core/.agentfs/<agent-id>.db`
- Base stamp: `alcatraz.core/.agentfs/<agent-id>.base-stamp`

The worker hashes `alcatraz.core/rootfs/etc/alcatraz-release` and refuses to silently reuse an overlay against a changed base rootfs. If the base image changed, either:
- use a new VM id, or
- the worker automatically reinitializes the overlay

## Runtime Notes

- Worker logs via `log/slog` to stdout **and** ships CLEF events to Seq when `SEQ_URL` is set (skipped silently if empty, so unit tests / spawn-client don't require Seq)
- Logging config (read from `.env`): `SEQ_URL`, `SEQ_API_KEY`, `ENVIRONMENT`. Baseline attributes `Application` and `Environment` are attached to every event. On shutdown the Seq queue is flushed (bounded by a 2s context)
- On VM exit, SDK's `doCleanup()` handles CNI teardown (TAP, IP, namespace); worker shuts down the in-process NFS server and removes the firecracker socket
- If the host `firecracker` binary is missing, worker tries to resolve from PATH
- AgentFS uses the Go SDK directly — no external `agentfs` binary or daemon
- Worker uses queue-based subscription for load balancing across multiple workers
- Slot allocation is atomic; returns error if no slots available (max VMs reached)

See [docs/cni-migration.md](docs/cni-migration.md) for CNI networking architecture, [docs/remove-cli-commands.md](docs/remove-cli-commands.md) for the in-process AgentFS/NFS rewrite, [docs/agentfs-edit-flow.md](docs/agentfs-edit-flow.md) for how guest edits flow through the overlay and how to inspect or sync the delta DB, and [docs/network-isolation.md](docs/network-isolation.md) for historical network isolation notes.

## Useful Overrides

The worker reads NATS settings from env vars (see [Configuration](#configuration)):

```bash
NATS_URL=nats://nats.internal:4222 NATS_QUEUE_GROUP=prod-workers sudo -E ./bin/alcatraz-worker
```

`spawn-client` keeps its CLI flags:

```bash
./bin/spawn-client -id my-vm -vcpus 8 -mem 16384
./bin/spawn-client -vcpus 2 -mem 2048 -kernel-args "quiet"
```

## Tests

```bash
cd alcatraz.worker
make test
```

