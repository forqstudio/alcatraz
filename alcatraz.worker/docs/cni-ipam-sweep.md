# CNI IPAM Startup Sweep

## What it does

At worker startup, `vm.SweepIPAM` (`internal/vm/cni_sweep.go`) deletes every
file in `/var/lib/cni/networks/alcatraz-bridge/` except the IPAM `lock` file —
both the per-IP lease files (`172.16.0.10`, `172.16.0.11`, …) and the
`last_reserved_ip.0` allocator marker.

It is invoked from `cmd/alcatraz-worker/main.go` before the VM service or the
NATS subscriber are constructed, so it runs while the worker is guaranteed to
own no live VMs.

## Why it exists

Two separate behaviours of the host-local IPAM plugin made the IP allocation
look "wrong" across worker restarts.

### 1. Stale leases survive ungraceful shutdowns

The per-VM cleanup goroutine added in
[`cni-cleanup-fix.md`](./cni-cleanup-fix.md) only releases CNI resources when
`m.Wait()` returns *and* the goroutine gets to finish. That works for graceful
VM exits and for the new `VirtualMachineService.Shutdown` path on SIGINT/SIGTERM,
but it cannot run if the worker is `kill -9`'d, segfaults, or is killed by
OOM. In those cases the lease files in
`/var/lib/cni/networks/alcatraz-bridge/` remain on disk and the host-local
plugin treats them as live allocations forever.

Symptom: after a few crashes, IPs `.10–.14` (or whichever happened to be in
use) become permanently "taken", and the next worker run starts allocating
from `.15` upward.

### 2. `last_reserved_ip.0` is sequential, not lowest-free

Even with a perfectly clean IPAM dir, host-local does not allocate the lowest
free IP. It records the most recently allocated IP in `last_reserved_ip.0`
and, on each ADD, scans forward from `last+1`, wrapping at `rangeEnd` back to
`rangeStart`. The marker persists across worker restarts.

Symptom: stop the worker after spawning a VM at `.17`, restart it, and the
next VM gets `.18` instead of `.10` — even though `.10` is free and the
worker is freshly started.

This is by design in host-local, but it makes IP assignment look
non-deterministic from the operator's point of view, especially during
development where you restart the worker frequently and expect the first VM
to land on the first IP of the range.

## Why a startup sweep is the right fix

The sweep relies on a single invariant: **at worker startup, no VM that this
worker owns is alive**. That makes every entry in our network's IPAM dir
necessarily one of:

- a stale lease from a previous run (case 1), or
- the `last_reserved_ip.0` marker from a previous run (case 2).

Both should be discarded. The sweep does not need to inspect lease contents,
correlate with running processes, or distinguish "stale" from "live", because
on a fresh start there are no live entries to protect.

The runtime cleanup path (per-VM goroutine + `Shutdown`) keeps the dir
consistent *during* a worker run. The sweep is a belt-and-suspenders
guarantee for the *between-runs* gap, where the runtime path can't help
because the process isn't alive.

## Constraints and assumptions

- **Single worker per host.** The sweep wipes the IPAM dir for the
  `alcatraz-bridge` network unconditionally. Two workers sharing the same
  bridge would step on each other — but the worker isn't multi-instance-safe
  today anyway (the NFS port allocation in `vm_spawn.go` and `vm_service.go`
  hardcodes `8000 + index` from a per-process index pool, so two workers
  would already collide on port 8000). If multi-worker support is ever
  added, the sweep must become aware of other workers' live leases.
- **Network name is hardcoded.** `cniIPAMDir` in `cni_sweep.go` matches the
  `name` field in `/etc/cni/net.d/alcatraz-bridge.conflist`. If the CNI
  network is renamed, both must be updated.
- **The `lock` file is preserved.** It is created by host-local on first use
  and used to serialise ADD/DEL inside a single allocation. Removing it
  while the plugin is mid-allocation could cause a race, and there is no
  benefit to deleting it.

## Behaviour reference

CNI range (from `/etc/cni/net.d/alcatraz-bridge.conflist`):

```
rangeStart: 172.16.0.10
rangeEnd:   172.16.0.250
```

After a sweep + fresh worker run, IPs are assigned `.10`, `.11`, `.12`, … in
order, wrapping back to `.10` when `.250` is reached and a lower IP is free.
True exhaustion (all 241 IPs simultaneously held) surfaces as a CNI ADD
error from `m.Start`, but the worker's `DefaultMaxVMs = 5` slot pool will
refuse the spawn long before that.

## Related files

- `internal/vm/cni_sweep.go` — the sweep itself.
- `cmd/alcatraz-worker/main.go` — invocation site at worker startup.
- `internal/vm/vm_service.go` — `Shutdown` / cleanup `WaitGroup` for the
  graceful-exit path.
- `internal/vm/vm_spawn.go` — per-VM `m.Wait` cleanup goroutine.
- `docs/cni-cleanup-fix.md` — the earlier per-VM cleanup fix this builds on.
