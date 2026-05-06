# Network Isolation

> **Note:** The TAP + iptables architecture described in the historical sections below has been replaced by CNI-based networking via the Firecracker Go SDK. See [cni-migration.md](cni-migration.md) for the current design.

## Current Architecture (CNI Bridge)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ HOST                                                                         │
│                                                                              │
│  ┌───────────────────────────────────────────────────────────────────────┐   │
│  │ Bridge: alcatraz0 (172.16.0.1/24, isGateway)                          │   │
│  │                                                                        │   │
│  │   fc-tap0 ──── VM 0 (172.16.0.x)                                     │   │
│  │   fc-tap1 ──── VM 1 (172.16.0.x)                                     │   │
│  │   fc-tap2 ──── VM 2 (172.16.0.x)                                     │   │
│  │   ...                                                                  │   │
│  └────────────────────────────┬──────────────────────────────────────────┘   │
│                               │                                              │
│                        ipMasq (CNI bridge plugin)                            │
│                               │                                              │
│                               ↓                                              │
│                         Internet                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

- **Bridge**: CNI `bridge` plugin creates `alcatraz0` with `isGateway: true`
- **IPAM**: CNI `host-local` plugin allocates IPs from `172.16.0.10-250`
- **TAP**: CNI `tc-redirect-tap` plugin creates per-VM TAP devices (`fc-tap0`, `fc-tap1`, ...)
- **NAT**: Bridge plugin handles masquerade (`ipMasq: true`)
- **Cleanup**: SDK's `doCleanup()` invokes CNI DEL on VM exit to release all resources
- **NFS**: AgentFS NFS server binds to gateway IP `172.16.0.1`, port `8000 + slot index`

All VMs share the bridge and can communicate with each other and the internet. The CNI conflist is at `cni/alcatraz-bridge.conflist`.

### Known Limitation: No Cross-VM Isolation

**Status:** VMs on the `alcatraz0` bridge can reach each other's services.

**Tested 2026-05-06:**
```
# From VM at 172.16.0.12, connecting to VM at 172.16.0.11:
al@alcatraz:~$ cat < /dev/tcp/172.16.0.11/22
SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13
```

The CNI bridge plugin does not provide inter-VM isolation. Unlike the previous iptables-based architecture, there are no FORWARD DROP rules between TAP interfaces. Any VM can connect to any other VM's open ports via the shared bridge.

**Impact:** Untrusted agents running in one VM can probe or connect to services (e.g., SSH, NFS) on other VMs on the same worker.

**Why no CNI plugin can fix this:** L2 traffic between VMs on the same bridge is switched inside the kernel bridge and never reaches iptables FORWARD. No standard CNI plugin (firewall, tuning, bridge flags) provides same-bridge isolation. Docker's `--icc=false` is daemon behavior, not a CNI plugin. Calico/Cilium are full Kubernetes CNI replacements -- overkill here.

**Recommended mitigation: `br_netfilter` + static iptables rule**

Enable `br_netfilter` so bridged L2 traffic passes through iptables, then add a single bridge-wide DROP rule:

```bash
# Enable bridge netfilter (run once at boot or worker start)
modprobe br_netfilter
sysctl -w net.bridge.bridge-nf-call-iptables=1

# Block all VM-to-VM traffic on the bridge
iptables -A FORWARD -i alcatraz0 -o alcatraz0 -j DROP
```

This works because:
- **VM-to-VM** traffic is `-i alcatraz0 -o alcatraz0` -- blocked by the DROP rule
- **VM-to-gateway** (172.16.0.1) is local to the bridge interface -- not forwarded, unaffected
- **VM-to-internet** is `-i alcatraz0 -o <host-iface>` -- does not match the rule, unaffected
- **No per-VM cleanup needed** -- the rule is bridge-wide and static

**To persist across reboots:**

```bash
# 1. Load br_netfilter on boot
echo "br_netfilter" | sudo tee /etc/modules-load.d/br_netfilter.conf

# 2. Enable bridge netfilter on boot
echo "net.bridge.bridge-nf-call-iptables=1" | sudo tee /etc/sysctl.d/99-bridge-nf.conf

# 3. Persist the iptables rule
sudo apt install iptables-persistent   # if not already installed
sudo iptables -A FORWARD -i alcatraz0 -o alcatraz0 -j DROP
sudo netfilter-persistent save
```

**Other options considered:**
- Per-VM bridges (separate L2 segments) -- works but adds complexity
- CNI `firewall` plugin -- only does per-container anti-spoofing, not peer isolation
- CNI `bridge` plugin flags -- no isolation flag exists

---

## Historical: Original Architecture (Pre-Isolation)

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

### Why Additional Isolation?

Firecracker VMs are already isolated from each other internally - each VM runs its own kernel with its own network stack. However, on the **host side**, all TAP devices (`fc-tap0`, `fc-tap1`, etc.) live in the same host network namespace.

**Without iptables rules:**
```
Host root NS:  fc-tap0  ←──────  fc-tap1  (could communicate via host)
                ↓                    ↓
            172.16.0.2          172.16.1.2
```

The iptables rules prevent host-side bridging/ARP between TAP interfaces - they block traffic at the host level before it can reach other VMs.

## Historical: TAP + iptables Architecture

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

### Key Changes (from original to iptables)

1. **VM-level isolation** - Firecracker already isolates VMs from each other internally
2. **Host-level isolation** - iptables rules block cross-VM traffic at TAP interface level
3. **NAT** - Shared, enables internet access for all VMs

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

| Aspect            | Original (TAP) | iptables | CNI Bridge (Current) | veth + NS (Attempted) |
|-------------------|----------------|----------|---------------------|-----------------------|
| Cross-VM access   | ✅ Possible | ❌ Blocked | ✅ Possible (shared bridge) | ❌ Impossible |
| Network namespace | Host root | Host root | CNI-managed | Per-VM |
| NAT location      | Host root | Host root | CNI bridge plugin (ipMasq) | Per-VM |
| Firecracker compat| ✅ | ✅ | ✅ | ❌ (veth not supported) |
| NFS isolation     | Host root | Host root | Host root (bridge gateway) | In namespace |
| Implementation    | N/A | Simple | SDK-managed | Failed (incompatible) |

## Future Improvements

If Firecracker adds veth support, consider:

1. **Full network namespace isolation** - Each VM gets its own namespace with veth pair
2. **NFS in namespace** - Run agentfs NFS inside VM's namespace via `ip netns exec`
3. **Per-VM iptables** - Rules applied only within that namespace

## Configuration

The number of slots is configurable via `--max-vms`:

```bash
# Default: 5 slots
# Each slot gets:
#   - TAP: fc-tap{0..N}
#   - VM IP: dynamic from 172.16.0.10-250 (CNI host-local IPAM)
#   - Gateway: 172.16.0.1 (bridge)
#   - NFS port: 8000 + index
```

Current config supports ~240 VMs. To scale further, change the subnet to `/16` in `cni/alcatraz-bridge.conflist`.