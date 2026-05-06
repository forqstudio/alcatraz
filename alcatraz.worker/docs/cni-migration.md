# CNI Integration via Firecracker Go SDK

## Overview

This document describes the integration of CNI-based networking using the Firecracker Go SDK's built-in CNI support. The SDK automatically handles CNI invocation, TAP device creation, and network namespace setup.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     alcatraz-worker                         │
│  ┌─────────────┐    ┌──────────────────────────────────┐   │
│  │  VM Request │───▶│  Firecracker Go SDK             │   │
│  └─────────────┘    │  (CNIConfiguration)             │   │
│                      └──────────────┬───────────────────┘   │
│                                     │                       │
│                                     ▼                       │
│                      ┌──────────────────────────────────┐   │
│                      │  CNI Plugins (tc-redirect-tap)  │   │
│                      │  - bridge                       │   │
│                      │  - host-local (IPAM)            │   │
│                      │  - tc-redirect-tap              │   │
│                      └──────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

## Multiple VM Support

The CNI-based setup fully supports running multiple VMs concurrently. Here's how it works:

### Unique VM Identification
Each VM gets:
- **Unique VMID** (from `instance.id`) → used as CNI `containerID`
- **Unique TAP device** (e.g., `fc-tap0`, `fc-tap1`, `fc-tap2`) → passed as CNI `IfName`

From `vm_spawn.go`:
```go
tapDev := fmt.Sprintf("fc-tap%d", index)
// ...
CNIConfiguration: &firecracker.CNIConfiguration{
    NetworkName: "alcatraz-bridge",
    IfName:      tapDev,  // Unique per VM
    VMIfName:    "eth0",
    ...
}
```

### CNI IPAM Handles Multiple IPs
The `host-local` plugin in `cni/alcatraz-bridge.conflist`:
- Allocates IPs from `172.16.0.10-250` (~240 available IPs)
- Tracks allocations per `containerID` + `ifName` in `/var/lib/cni/networks/alcatraz-bridge/`
- Each VM automatically gets a different IP

### Bridge Networking
The `bridge` plugin:
- Creates shared bridge `alcatraz0` (first VM creates it, others join)
- Attaches each VM's TAP device to the bridge
- All VMs share outbound internet access
- **Note:** VMs can communicate with each other via the shared bridge -- there is no inter-VM isolation (see [network-isolation.md](network-isolation.md))

### VM Slot Management
The `IntPool` in `vm_service.go`:
- Manages available VM slots (0 to maxVMs-1)
- Ensures unique indices are allocated
- Releases indices when VMs stop

### IP Allocation Flow Example
```
VM 1 (fc-tap0) → CNI → host-local → 172.16.0.10
VM 2 (fc-tap1) → CNI → host-local → 172.16.0.11
VM 3 (fc-tap2) → CNI → host-local → 172.16.0.12
...
```

### Scaling Limits
- **Current config**: ~240 VMs (IP range `172.16.0.10-250`)
- **To support more**: Edit `cni/alcatraz-bridge.conflist`:
  - Change subnet to `/16` for 65,000+ IPs: `"subnet": "172.16.0.0/16"`
  - Adjust `rangeStart` and `rangeEnd` accordingly

---

## Key Components

### 1. CNI Configuration (`cni/alcatraz-bridge.conflist`)

Uses CNI list format with chained plugins:
- **bridge**: Creates a bridge device (`alcatraz0`)
- **host-local**: Handles IP address management
- **tc-redirect-tap**: Creates TAP device for Firecracker VM

```json
{
  "cniVersion": "0.4.0",
  "name": "alcatraz-bridge",
  "plugins": [
    {
      "type": "bridge",
      "bridge": "alcatraz0",
      "isGateway": true,
      "ipMasq": true,
      "ipam": {
        "type": "host-local",
        "subnet": "172.16.0.0/24",
        "rangeStart": "172.16.0.10",
        "rangeEnd": "172.16.0.250",
        "routes": [
          {
            "dst": "0.0.0.0/0"
          }
        ]
      }
    },
    {
      "type": "tc-redirect-tap"
    }
  ]
}
```

### 2. VM Configuration (`internal/vm/vm_spawn.go`)

Uses `CNIConfiguration` instead of manual network setup:

```go
cfg := firecracker.Config{
    ...
    NetworkInterfaces: []firecracker.NetworkInterface{
        {
            CNIConfiguration: &firecracker.CNIConfiguration{
                NetworkName: "alcatraz-bridge",
                IfName:      tapDev,      // e.g., "fc-tap0"
                VMIfName:    "eth0",      // interface name inside VM
                ConfDir:     "/etc/cni/net.d",
                BinPath:     []string{"/opt/cni/bin"},
            },
        },
    },
    VMID: instance.id,
}
```

The SDK automatically:
- Invokes CNI plugins
- Creates network namespace
- Sets up TAP device
- Configures IP addresses
- Generates `ip=` kernel boot parameter

## Implementation Changes

### Files Removed
| File | Reason |
|------|--------|
| `internal/vm/network.go` | Manual network setup replaced by CNI |
| `pkg/cni/cni.go` | Custom IPAM replaced by CNI host-local |

### Files Modified
| File | Change |
|------|--------|
| `internal/vm/vm_spawn.go` | Use `CNIConfiguration`, remove `SetupTAPDev()`, `SetupNAT()`, `SetupIsolationRules()` |
| `internal/vm/vm_service.go` | Remove `ipamClient`, `AllocateNetwork()`, `ReleaseNetwork()` |

### Flow: VM Spawn (New)

```
1. Allocate index from pool
2. Configure VM with CNIConfiguration
3. Prepare AgentFS overlay
4. Start AgentFS NFS
5. SDK invokes CNI, creates TAP, starts VM
6. CNI automatically handles:
   - Network namespace creation
   - TAP device creation (via tc-redirect-tap)
   - IP allocation (via host-local)
   - NAT/masquerade (via bridge plugin)
```

### Flow: VM Cleanup (New)

```
1. VM exits
2. SDK automatically invokes CNI DEL
3. CNI cleans up:
   - TAP device removal
   - Network namespace cleanup
   - IP release
4. Release index back to pool
```

## Prerequisites

### Required CNI Plugins

Install to `/opt/cni/bin`:

1. **Standard CNI plugins** (bridge, host-local, etc.):
   ```bash
   curl -sL https://github.com/containernetworking/plugins/releases/download/v1.0.1/cni-plugins-linux-amd64-v1.0.1.tgz | \
     tar -xz -C /opt/cni/bin/
   ```

2. **tc-redirect-tap** (Firecracker-specific):
   ```bash
   git clone https://github.com/firecracker-microvm/tc-redirect-tap.git
   cd tc-redirect-tap
   make
   cp tc-redirect-tap /opt/cni/bin/
   ```

### CNI Configuration

Install the CNI config file:
```bash
mkdir -p /etc/cni/net.d
cp cni/alcatraz-bridge.conflist /etc/cni/net.d/
```

## Benefits

| Before | After |
|--------|-------|
| Manual iptables/ip commands | CNI plugins handle networking |
| Custom IPAM implementation | Standard CNI host-local IPAM |
| Manual TAP device creation | tc-redirect-tap creates TAP automatically |
| No network namespace support | CNI manages network namespaces |
| Custom cleanup code | CNI DEL automatically cleans up |
| Firecracker Go SDK | Firecracker Go SDK (CNIConfiguration) |

## Kernel Boot Arguments

The SDK automatically generates the `ip=` parameter from CNI results. For NFS root, manually specify NFS options:

```go
bootArgs := fmt.Sprintf(
    "console=ttyS0 reboot=k panic=1 pci=off %s root=/dev/nfs nfsroot=${GATEWAY_IP}:/,nfsvers=3,tcp,nolock,port=%d,mountport=%d rw init=/init",
    instance.kernelArgs,
    instance.nfsPort,
    instance.nfsPort,
)
```

Note: `${GATEWAY_IP}` is replaced by the SDK with the gateway IP from CNI result.

## Deployment

```bash
# Build
go build -o bin/alcatraz-worker ./cmd/alcatraz-worker

# Run (requires CAP_NET_ADMIN, CAP_SYS_ADMIN)
sudo ./bin/alcatraz-worker
```

## Testing Multiple VMs

To verify multiple VM support:

```bash
# Check VM slot allocation
alcatraz-worker spawn --id test-vm1
alcatraz-worker spawn --id test-vm2
alcatraz-worker spawn --id test-vm3

# List running VMs
alcatraz-worker list

# Check CNI IP allocations
cat /var/lib/cni/networks/alcatraz-bridge/*

# Cleanup
alcatraz-worker stop --id test-vm1
alcatraz-worker stop --id test-vm2
alcatraz-worker stop --id test-vm3
```

### Verify CNI Plugin Functionality

```bash
# Check bridge device exists
ip link show alcatraz0

# Check TAP devices created per VM
ip link show | grep fc-tap

# Verify IP allocations in CNI state
ls -la /var/lib/cni/networks/alcatraz-bridge/
```
