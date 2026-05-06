# SSH Connection Issue - Root Cause Analysis

> **Note:** This document was written during the CNI migration. Some issues described (stale TAP devices, route conflicts) are now handled by the SDK's CNI cleanup (`doCleanup()` invoking CNI DEL). Retained for historical reference. See [cni-cleanup-fix.md](cni-cleanup-fix.md) for the cleanup fix details.

## Symptoms
- VM spawns successfully, gets IP (e.g., 172.16.0.10)
- SSH connection fails: "No route to host" / "Destination Host Unreachable"
- ARP shows duplicate/incomplete entries for the VM IP

## Root Causes

### 1. Subnet Change Breaking Routing (Primary Issue)
**Changed behavior**: All VMs now share subnet `172.16.0.0/24` instead of per-VM `/24` subnets.

**Problem**: Multiple TAP devices accumulate with the same route:
```
172.16.0.0/24 dev fc-tap0 src 172.16.0.1
172.16.0.0/24 dev fc-tap1 src 172.16.0.1  <- stale
172.16.0.0/24 dev fc-tap2 src 172.16.0.1  <- stale
...
```

The kernel routes traffic to the wrong (stale) TAP device, never reaching the VM.

### 2. NFS Root Mount Dependency
**Default kernel args**: `eth0:off` - VM expects NFS root to mount BEFORE configuring network.

**Problem**: If NFS mount fails early in boot, the VM init process stalls/fails before eth0 is configured → no network connectivity.

The agentfs NFS server doesn't register with Linux portmapper, so mount.nfs fails from the host. The VM panics or hangs during early boot.

### 3. TAP Device Leakage
TAP devices aren't being deleted when VMs exit (permission issues without sudo).

## Fixes Applied

### Fix 1: Remove eth0:off dependency
In `internal/vm/vm_spawn.go`, change:
```
eth0:off  ->  eth0
```

This allows the kernel to configure networking even if NFS root mount fails.

### Fix 2: Clean up stale TAP devices
Between tests, run:
```bash
sudo ip link del fc-tap0
sudo ip link del fc-tap1
# ... for all fc-tap devices
```

### Fix 3: Clear CNI state
```bash
echo -n | sudo tee /var/lib/cni/networks/alcatraz-bridge
```

## Long-term Recommendations

1. **Use proper NFS or eliminate NFS root** - Consider:
   - Using kernel nfsd instead of agentfs NFS
   - Using initramfs with networking already configured
   - Using a different rootfs (e.g., 9p VirtFS)

2. **Fix TAP device cleanup** - Ensure CleanupTap runs with proper permissions

3. **Route cleanup** - Explicitly delete routes before creating new TAP devices

4. ~~**Consider veth pairs**~~ - Resolved: CNI-based networking was adopted instead (see [cni-migration.md](cni-migration.md))