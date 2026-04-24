# Alcatraz.Worker

The `alcatraz-worker` is a Go application that listens to NATS messages to spawn Firecracker VMs dynamically. It supports multiple concurrent VMs, each with isolated networking via TAP devices with iptables-based isolation and NAT.

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
3. Worker creates a TAP device and configures IP/NAT for the allocated subnet
4. Worker adds iptables rules to block cross-VM traffic for network isolation
5. Worker initializes or reuses an AgentFS overlay in `.agentfs/<agent-id>.db`
6. Worker starts AgentFS NFS server exporting the overlay on the host TAP IP
7. Worker spawns Firecracker VM with root=/dev/nfs pointing to the AgentFS NFS export
8. On VM exit, worker cleans up TAP device, NAT rules, isolation rules, and NFS process

The practical effect is:
- VMs can be spawned on-demand via NATS messages
- Multiple workers can handle load balancing via queue groups
- Each VM gets isolated network stack with its own TAP/subnet/NFS
- Network isolation between VMs - agents cannot reach other VMs' network interfaces

## Host Requirements

You need these on the host:
- Ubuntu or another Linux host with `sudo`
- KVM / Firecracker support
- `agentfs` installed on the host and available on `PATH`
- `firecracker` binary available (auto-resolved to v1.15.1 or PATH)
- NATS server running 
- `iptables` available for NAT
- package install permissions for `ip`, `iptables`, `sysctl`

The worker requires `sudo` to create TAP devices and set up NAT rules.

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
- Instance manager with slot allocation
- TAP device creation and teardown
- NAT setup and cleanup
- iptables-based cross-VM isolation rules
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

Each VM gets allocated a unique subnet:

| Slot | TAP Device | Host IP   | Guest IP  | NFS Port |
|------|-----------|-----------|-----------|----------|
| 0    | fc-tap0   | 172.16.0.1 | 172.16.0.2 | 11111  |
| 1    | fc-tap1   | 172.16.1.1 | 172.16.1.2 | 11112  |
| 2    | fc-tap2   | 172.16.2.1 | 172.16.2.2 | 11113  |
| 3    | fc-tap3   | 172.16.3.1 | 172.16.3.2 | 11114  |
| 4    | fc-tap4   | 172.16.4.1 | 172.16.4.2 | 11115  |

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
ssh dev@172.16.0.2
```

Default password: `dev`

## Persistence Model

- Base image: `alcatraz.core/rootfs`
- Overlay DB: `alcatraz.core/.agentfs/<agent-id>.db`
- Base stamp: `alcatraz.core/.agentfs/<agent-id>.base-stamp`

The worker hashes `alcatraz.core/rootfs/etc/alcatraz-release` and refuses to silently reuse an overlay against a changed base rootfs. If the base image changed, either:
- use a new VM id, or
- the worker automatically reinitializes the overlay

## Runtime Notes

- Worker logs to stdout by default with go logrus
- On VM exit, worker automatically cleans up: NFS process, TAP device, NAT rules, isolation rules, socket
- If the host `firecracker` binary is missing, worker tries to resolve from PATH
- The host `agentfs` binary is auto-resolved from PATH or common locations
- Worker uses queue-based subscription for load balancing across multiple workers
- Slot allocation is atomic; returns error if no slots available (max VMs reached)
- Cross-VM traffic is blocked via iptables DROP rules - agents cannot access other VMs' network

See [docs/NETWORK_ISOLATION.md](docs/NETWORK_ISOLATION.md) for detailed network topology and isolation architecture.

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

