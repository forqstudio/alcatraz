# Network Isolation Migration

## Overview

This document describes the network isolation architecture for Firecracker microVMs and the migration from the original TAP-based setup to the current iptables-based isolation.

## Original Architecture (Pre-Migration)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ HOST ROOT NETWORK NAMESPACE                                                  │
│                                                                              │
│  ┌───────────────────────────────────────────────────────────────────────┐   │
│  │ TAP device: fc-tap{i}                                                 │   │
│  │ Host IP: 172.16.{i}.1/24                                              │   │
│  └────────────────────────────┬──────────────────────────────────────────┘   │
│                               │                                              │
│  ┌────────────────────────────▼──────────────────────────────────────────┐   │
│  │ IPTABLES RULES                                                         │   │
│  │   - NAT: POSTROUTING -s 172.16.{i}.0/24 -j MASQUERADE                │   │
│  │   - FORWARD: host-iface ↔ fc-tap{i} (RELATED,ESTABLISHED, ACCEPT)    │   │
│  │   - NO isolation rules (cross-VM traffic allowed)                    │   │
│  └───────────────────────────────────────────────────────────────────────┘   │
│                               │                                              │
│                               ↓                                              │
│                         Internet                                             │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│ FIRECRACKER VM                                                               │
│   eth0: 172.16.{i}.2/24                                                     │
│   gateway: 172.16.{i}.1                                                     │
│   nfsroot=172.16.{i}.1:/                                                   │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Issues
- All VMs in **same** network namespace (host root)
- Cross-VM communication possible via ARP/bridge
- Single point of failure for network rules
- No isolation between VMs' network stacks

## Current Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ HOST ROOT NETWORK NAMESPACE                                                  │
│                                                                              │
│  ┌───────────────────────────────────────────────────────────────────────┐   │
│  │ TAP device: fc-tap{i}                                                 │   │
│  │ Host IP: 172.16.{i}.1/24                                              │   │
│  └────────────────────────────┬──────────────────────────────────────────┘   │
│                               │                                              │
│  ┌────────────────────────────▼──────────────────────────────────────────┐   │
│  │ IPTABLES RULES                                                         │   │
│  │   - NAT: POSTROUTING -s 172.16.{i}.0/24 -j MASQUERADE                │   │
│  │   - FORWARD: host-iface ↔ fc-tap{i} (RELATED,ESTABLISHED, ACCEPT)    │   │
│  │   - ISOLATION: FORWARD -i fc-tap{i} -o fc-tap{j} -j DROP (all pairs) │   │
│  └────────────────────────────┬──────────────────────────────────────────┘   │
│                               │                                              │
│                               ↓                                              │
│                         Internet                                             │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│ FIRECRACKER VM                                                               │
│   eth0: 172.16.{i}.2/24                                                     │
│   gateway: 172.16.{i}.1                                                     │
│   nfsroot=172.16.{i}.1:/                                                   │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Key Changes

1. **TAP devices** - Still in root namespace (Firecracker requirement)
2. **NAT** - Shared, enables internet access for all VMs
3. **Isolation rules** - iptables DROP rules prevent cross-VM traffic

### Isolation Rules Added

```bash
# Block traffic from this VM to itself (loopback-like)
iptables -A FORWARD -i fc-tap0 -o fc-tap0 -j DROP

# Block traffic between all VM pairs
for i in 0..maxSlots-1:
  for j in 0..maxSlots-1:
    if i != j:
      iptables -A FORWARD -i fc-tap{i} -o fc-tap{j} -j DROP
```

### Cleanup

On VM exit, these rules are cleaned up:
```bash
# Remove NAT rules
iptables -t nat -D POSTROUTING -s 172.16.{i}.0/24 -o {hostIface} -j MASQUERADE

# Remove FORWARD rules
iptables -D FORWARD -i {hostIface} -o fc-tap{i} -m state --state RELATED,ESTABLISHED -j ACCEPT
iptables -D FORWARD -i fc-tap{i} -o {hostIface} -j ACCEPT

# Remove isolation rules
iptables -D FORWARD -i fc-tap{i} -o fc-tap{i} -j DROP
iptables -D FORWARD -i fc-tap{i} -o fc-tap{j} -j DROP
iptables -D FORWARD -i fc-tap{j} -o fc-tap{i} -j DROP

# Delete TAP device
ip link del fc-tap{i}
```

## Why Not Network Namespaces?

The original plan used per-VM network namespaces with veth pairs:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ HOST ROOT NETWORK NAMESPACE                                                  │
│                                                                              │
│   ┌─────────────────────────────────────────────────────────────────────┐    │
│   │ PER-VM NETWORK NAMESPACE: vm-{agentId}                              │    │
│   │                                                                       │    │
│   │   ┌─────────────────┐      ┌────────────────────────────────────┐   │    │
│   │   │ veth{i}-host    │ ←──→ │ iptables NAT + FORWARD rules       │   │    │
│   │   │ 172.16.{i}.1/24 │      │ (isolated per-namespace)           │   │    │
│   │   └────────┬────────┘      └──────────────┬─────────────────────┘   │    │
│   │            │                               │                         │    │
│   │            │            [ip netns exec vm-{id} ...]                  │    │
│   │            ↓                               ↓                         │    │
│   │   ┌─────────────────────────────────────────────────────────────┐   │    │
│   │   │ agentfs serve nfs --bind 172.16.{i}.1 --port {nfsPort}      │   │    │
│   │   │ (runs inside VM namespace, binds to gateway IP)             │   │    │
│   │   └─────────────────────────────────────────────────────────────┘   │    │
│   │                                                                       │    │
│   └─────────────────────────────────────────────────────────────────────┘    │
│                                       ↑                                      │
│                              FORWARD to host eth0                            │
│                                       ↓                                      │
│                              Internet                                        │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│ FIRECRACKER VM (veth{i}-guest passed as tap device)                         │
│                                                                              │
│   eth0: 172.16.{i}.2/24                                                      │
│   gateway: 172.16.{i}.1                                                      │
│   nfsroot=172.16.{i}.1:/                                                    │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Issues Encountered

1. **Firecracker doesn't support veth devices**

   Firecracker only supports TUN/TAP devices, not veth pairs. When attempting to pass a veth device to Firecracker:

   ```
   Error: Open tap device failed: Error while creating ifreq structure
   Invalid TUN/TAP Backend provided by veth0-guest
   ```

2. **TAP in namespace approach**

   We tried keeping TAP in the root namespace and using route-based isolation, but this:
   - Required complex routing rules
   - Still shared the root namespace
   - Added latency and complexity

3. **NFS binding issues**

   Running NFS inside the VM's namespace would require binding to the namespaced IP (172.16.{i}.1), but:
   - The IP only exists inside the namespace
   - Would need `ip netns exec` wrapper for every NFS operation
   - Added complexity for process management

### Why iptables Isolation?

Given these constraints, we chose iptables-based isolation because:

| Requirement | veth + NS (Attempted) | iptables (Chosen) |
|-------------|----------------------|-------------------|
| Firecracker compatibility | ❌ No | ✅ Yes |
| Cross-VM isolation | ✅ Yes | ✅ Yes |
| Implementation complexity | High | Low |
| NFS in root namespace | ❌ Needs wrapper | ✅ Works |
| Full network stack isolation | ✅ Yes | ❌ Partial |
| Time to implement | Days | Hours |

**Trade-off**: The iptables approach doesn't provide full network namespace isolation (each VM doesn't get its own TCP/IP stack), but it:
- Works with Firecracker's TAP requirement
- Blocks cross-VM traffic effectively
- Is simpler to implement and maintain
- Keeps NFS in root namespace (acceptable trade-off)

For the threat model (untrusted agents running in VMs), the iptables approach provides sufficient isolation - agents cannot reach other VMs' network interfaces or services.

### Future Possibilities

If Firecracker adds veth support in the future:
1. Full network namespace isolation becomes viable
2. Each VM gets its own TCP/IP stack
3. NFS could run inside the namespace for better isolation
4. Complete network isolation between VMs

## Comparison

| Aspect            | Original | Current (iptables) | veth + NS (Attempted) |
|-------------------|----------|--------------------|-----------------------|
| Cross-VM access   | ✅ Possible | ❌ Blocked | ❌ Impossible |
| Network namespace | Host root | Host root | Per-VM |
| NAT location      | Host root | Host root | Per-VM |
| Firecracker compat| ✅ | ✅ | ❌ (veth not supported) |
| NFS isolation     | Host root | Host root | In namespace |
| Implementation    | N/A | Simple | Failed (incompatible) |

## Future Improvements

If Firecracker adds veth support, consider:

1. **Full network namespace isolation** - Each VM gets its own namespace with veth pair
2. **NFS in namespace** - Run agentfs NFS inside VM's namespace via `ip netns exec`
3. **Per-VM iptables** - Rules applied only within that namespace

## Configuration

The number of slots is configurable via `MAX_VMS` or `--max-vms`:

```bash
# Default: 5 slots
# Each slot gets:
#   - TAP: fc-tap{0..4}
#   - Host IP: 172.16.{i}.1
#   - Guest IP: 172.16.{i}.2
#   - NFS port: 11111 + i
```

The isolation rules automatically adapt to `MAX_VMS` - no hardcoded limits.