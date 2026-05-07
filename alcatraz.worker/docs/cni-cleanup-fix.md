# CNI Cleanup Fix

## Problem

When a VM (Firecracker microVM) was stopped, the CNI (Container Network Interface) resources were not being properly cleaned up:
- IP address files remained in `/var/lib/cni/networks/alcatraz-bridge/`
- Network namespaces remained in `/var/run/netns/`
- This caused IP exhaustion and resource leaks over time

## Root Cause

The issue was in `/home/dev/Workspace/alcatraz/alcatraz.worker/internal/vm/spawn.go` (then named `vm_spawn.go`) in the goroutine that handles VM exit:

```go
go func() {
    id := instance.id
    if err := m.Wait(ctx); err != nil {  // <-- PROBLEM: ctx might be cancelled
        log.Printf("VM %s wait error: %v", id, err)
    }
    log.Printf("VM %s exited", id)
    virtualMachineService.RemoveVirtualMachine(id)
    virtualMachineService.Release(index)
}()
```

The `ctx` (context) passed to `m.Wait(ctx)` was the same context from the `Spawn()` function. When `Spawn()` returned, this context could be cancelled, causing `Wait()` to return early with `context.Canceled` before the SDK's cleanup goroutine could run `doCleanup()`.

## How the Firecracker Go SDK Cleanup Works

From the SDK source (`firecracker-go-sdk@v0.22.0`):

1. **When VM starts** (`machine.go:524-544`):
   - A goroutine is started that calls `m.cmd.Wait()`
   - When the process exits, it calls `m.doCleanup()`
   - `doCleanup()` runs all cleanup functions in reverse order

2. **Cleanup functions** (`network.go:326-382`):
   - `setupNetwork()` adds CNI DEL as a cleanup function
   - When `doCleanup()` runs, it calls `cniPlugin.DelNetworkList()` which:
     - Removes IP allocation files from `/var/lib/cni/networks/alcatraz-bridge/`
     - Removes TAP devices
     - Cleans up network namespace

3. **`StopVMM()`** (`machine.go:581-597`):
   - Sends `SIGTERM` to the firecracker process
   - This causes `m.cmd.Wait()` to return
   - Which triggers `doCleanup()`

## The Fix

Changed the context passed to `m.Wait()` from the potentially-cancelled `ctx` to `context.Background()`:

```go
go func() {
    id := instance.id
    // Use background context so cleanup isn't cancelled when Spawn() returns
    waitCtx := context.Background()
    if err := m.Wait(waitCtx); err != nil {
        log.Printf("VM %s wait error: %v", id, err)
    }
    log.Printf("VM %s exited", id)
    virtualMachineService.RemoveVirtualMachine(id)
    virtualMachineService.Release(index)
}()
```

Also added a small delay and debug logging to verify cleanup:

```go
    log.Printf("VM %s exited, SDK should now run doCleanup() to release CNI resources", id)

    // Give SDK time to run doCleanup()
    time.Sleep(2 * time.Second)

    log.Printf("VM %s cleanup complete, removing from service", id)
```

## Testing

Two test scripts are provided:

1. **`scripts/test-cni-cleanup.sh`** - Interactive test (requires two terminals)
2. **`scripts/test-cni-cleanup-auto.sh`** - Automated test (runs in one terminal)

Run the automated test:
```bash
cd /home/dev/Workspace/alcatraz/alcatraz.worker
sudo ./scripts/test-cni-cleanup-auto.sh
```

Expected results:
- After spawn: IP file exists in `/var/lib/cni/networks/alcatraz-bridge/`
- After stop: IP file is **removed**
- TAP devices are cleaned up
- Network namespace is removed

## Files Changed

- `alcatraz.worker/internal/vm/spawn.go` (named `vm_spawn.go` at the time):
  - Added `context` and `time` imports
  - Changed `m.Wait(ctx)` to `m.Wait(context.Background())`
  - Added debug logging and small delay for cleanup verification

## Verification

Before fix:
```
$ sudo ls /var/lib/cni/networks/alcatraz-bridge/
172.16.0.10  last_reserved_ip.0  lock
```

After fix:
```
$ sudo ls /var/lib/cni/networks/alcatraz-bridge/
last_reserved_ip.0  lock
```

The IP file (`172.16.0.10`) is now properly removed when the VM stops.
