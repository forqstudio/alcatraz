# Alcatraz.Worker

The `alcatraz-worker` is a Go application that listens to NATS messages to spawn Firecracker VMs dynamically. It supports multiple concurrent VMs, each with CNI-based networking via the Firecracker Go SDK and a shared bridge (`alcatraz0`).

This repository contains the build and launch scripts only. Generated artifacts such as `bin/`, `alcatraz.core/` symlinks, and log files are intentionally excluded from version control.

## The Stack

- Go: `1.25.0`
- Docker: with Docker Compose (for NATS)
- NATS: `latest` (with JetStream)
- Firecracker target: `v1.15.1`
- AgentFS target: `v0.6.4`

## How It Works

1. Worker subscribes to NATS `vm.spawn` subject with queue group for load balancing
2. On VM request, worker allocates an available slot
3. Worker configures `CNIConfiguration` for the Firecracker SDK; on VM start, the SDK invokes CNI plugins (bridge, host-local IPAM, tc-redirect-tap) which handle TAP creation, IP allocation, and NAT automatically
4. Worker initializes or reuses an AgentFS overlay in `.agentfs/<agent-id>.db`
5. Worker starts AgentFS NFS server exporting the overlay on the bridge gateway IP
6. Worker spawns Firecracker VM with root=/dev/nfs pointing to the AgentFS NFS export
7. On VM exit, the SDK's `doCleanup()` invokes CNI DEL to release TAP, IP, and namespace; worker cleans up NFS process and releases the slot

The practical effect is:
- VMs can be spawned on-demand via NATS messages
- Multiple workers can handle load balancing via queue groups
- Each VM gets a unique IP on a shared bridge with NFS root via AgentFS
- CNI plugins handle all networking setup and teardown automatically
- **Note:** VMs on the same worker can currently communicate with each other via the shared bridge (see [docs/network-isolation.md](docs/network-isolation.md))

## Host Requirements

You need these on the host:
- Ubuntu or another Linux host with `sudo`
- KVM / Firecracker support
- `agentfs` installed on the host and available on `PATH`
- `firecracker` binary available (auto-resolved to v1.15.1 or PATH)
- NATS server running
- CNI plugins installed at `/opt/cni/bin` (bridge, host-local, tc-redirect-tap)
- CNI conflist installed at `/etc/cni/net.d/alcatraz-bridge.conflist`

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
- `alcatraz.core/` with built kernel and rootfs available
- Worker references these paths from `../alcatraz.core/`:
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
- AgentFS overlay initialization
- AgentFS NFS server process
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

Start NATS:

```bash
docker compose up -d
```

Start the worker (must run as root):

```bash
sudo ./bin/alcatraz-worker
```

## CLI Flags

```bash
--nats-url string       NATS URL (default "nats://localhost:4222")
--subject string       NATS subject (default "vm.spawn")
--max-vms int          Max concurrent VMs (default 5)
--queue-group string   NATS queue group (default "vm-workers")
--agentfs-bin string   Path to agentfs binary (auto-resolved)
--firecracker-bin      Path to firecracker (auto-resolved to v1.15.1)
--rootfs string        Rootfs path (default "../alcatraz.core/rootfs")
--kernel string        Kernel path (default "../alcatraz.core/linux-amazon/vmlinux")
--agentfs-dir string   AgentFS overlay directory (default "../alcatraz.core/.agentfs")
```

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

```bash
ssh dev@<VM_IP>
```

The VM IP is logged by the worker at spawn time (e.g., `172.16.0.10`). Default password: `dev`

## Persistence Model

- Base image: `alcatraz.core/rootfs`
- Overlay DB: `alcatraz.core/.agentfs/<agent-id>.db`
- Base stamp: `alcatraz.core/.agentfs/<agent-id>.base-stamp`

The worker hashes `alcatraz.core/rootfs/etc/alcatraz-release` and refuses to silently reuse an overlay against a changed base rootfs. If the base image changed, either:
- use a new VM id, or
- the worker automatically reinitializes the overlay

## Runtime Notes

- Worker logs to stdout by default with go logrus
- On VM exit, SDK's `doCleanup()` handles CNI teardown (TAP, IP, namespace); worker cleans up NFS process and socket
- If the host `firecracker` binary is missing, worker tries to resolve from PATH
- The host `agentfs` binary is auto-resolved from PATH or common locations
- Worker uses queue-based subscription for load balancing across multiple workers
- Slot allocation is atomic; returns error if no slots available (max VMs reached)

See [docs/cni-migration.md](docs/cni-migration.md) for CNI networking architecture and [docs/network-isolation.md](docs/network-isolation.md) for historical network isolation notes.

## Useful Overrides

```bash
sudo ./bin/alcatraz-worker --max-vms 10 --nats-url nats://nats.internal:4222
sudo ./bin/alcatraz-worker --queue-group prod-workers --agentfs-bin /usr/local/bin/agentfs
```

```bash
./bin/spawn-client -id my-vm -vcpus 8 -mem 16384
./bin/spawn-client -vcpus 2 -mem 2048 -kernel-args "quiet"
```

## Tests

```bash
cd alcatraz.worker
make test
```

