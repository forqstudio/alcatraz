# Manual CNI Cleanup Test

Since automated tests have issues with sudo/permissions, here are manual steps to test the CNI cleanup fix.

## Prerequisites
- Build the worker: `cd /home/dev/Workspace/alcatraz/alcatraz.worker && make build`
- You need sudo/root access

## Terminal 1: Start Worker
```bash
cd /home/dev/Workspace/alcatraz/alcatraz.worker
sudo ./bin/alcatraz-worker
```

## Terminal 2: Run Test Steps

### Step 1: Clean Environment
```bash
# Clean up any previous state
sudo rm -f /var/lib/cni/networks/alcatraz-bridge/172.16.0.*
sudo rm -f /var/lib/cni/networks/alcatraz-bridge/last_reserved_ip.0
sudo pkill -9 -f alcatraz-worker 2>/dev/null || true
sudo pkill -9 -f firecracker 2>/dev/null || true
sleep 2

# Verify clean state
echo "=== Clean State ==="
sudo ls -la /var/lib/cni/networks/alcatraz-bridge/
ip link show alcatraz0 2>&1 || echo "No bridge (expected)"
```

### Step 2: Spawn VM
```bash
cd /home/dev/Workspace/alcatraz/alcatraz.worker
./bin/spawn-client --id test-manual --vcpus 1 --mem 512

# Wait for VM to start
sleep 10

# Check resources allocated
echo "=== After Spawn ==="
sudo ls -la /var/lib/cni/networks/alcatraz-bridge/
ip link show alcatraz0
ip link show | grep fc-tap
```

**Expected:** 
- IP file (e.g., `172.16.0.10`) exists in CNI state
- Bridge `alcatraz0` is UP
- TAP device `fc-tap0` exists

### Step 3: Stop VM
```bash
# Find and stop the firecracker process
FIRECRACKER_PID=$(ps aux | grep "firecracker.*test-manual" | grep -v grep | awk '{print $2}')
echo "Stopping Firecracker (PID: $FIRECRACKER_PID)..."
sudo kill -TERM $FIRECRACKER_PID

# Wait for cleanup
sleep 10
```

### Step 4: Verify Cleanup
```bash
echo "=== After Stop ==="
sudo ls -la /var/lib/cni/networks/alcatraz-bridge/
ip link show | grep fc-tap || echo "TAP devices cleaned up ✓"
sudo ls -la /var/run/netns/ | grep test-manual || echo "Netns cleaned up ✓"
ip link show alcatraz0
```

**Expected:**
- NO IP files (like `172.16.0.10`) in CNI state - they should be removed
- TAP devices cleaned up
- Network namespace cleaned up
- Bridge `alcatraz0` persists but may be DOWN

## What Was Fixed

The issue was in `/home/dev/Workspace/alcatraz/alcatraz.worker/internal/vm/spawn.go` (then named `vm_spawn.go`):

**Before:**
```go
go func() {
    id := instance.id
    if err := m.Wait(ctx); err != nil {  // ctx could be cancelled!
        log.Printf("VM %s wait error: %v", id, err)
    }
    // Cleanup might not happen if ctx is cancelled
}()
```

**After:**
```go
go func() {
    id := instance.id
    // Use background context so cleanup isn't cancelled when Spawn() returns
    waitCtx := context.Background()
    if err := m.Wait(waitCtx); err != nil {
        log.Printf("VM %s wait error: %v", id, err)
    }
    log.Printf("VM %s exited, SDK should now run doCleanup()", id)
    time.Sleep(2 * time.Second)  // Give SDK time to run cleanup
}()
```

The Firecracker Go SDK's `doCleanup()` calls CNI DEL which removes:
- IP allocation files from `/var/lib/cni/networks/alcatraz-bridge/`
- TAP devices
- Network namespaces

## Files Changed
- `alcatraz.worker/internal/vm/spawn.go` (named `vm_spawn.go` at the time) - Fixed context issue
- `alcatraz.worker/scripts/test-cni-cleanup.sh` - Interactive test
- `alcatraz.worker/scripts/test-cni-cleanup-auto.sh` - Automated test
- `alcatraz.worker/docs/cni-cleanup-fix.md` - Documentation
