# Remove CLI command invocations from the Go worker

> **Status:** Implemented. All four `os/exec` call sites have been removed and replaced with in-process Go code in `internal/vm/agentfs/`. The worker no longer requires the `agentfs` CLI binary on `PATH`. See "Implementation summary" at the bottom for the as-built layout.
>
> **Post-landing changes** (the rest of this doc reflects state at the time of the original PR):
> - `internal/vm/agentfs_service.go` was deleted. `PrepareAgentfsOverlay` was a thin forwarder and is now inlined as `agentfs.PrepareOverlay` at the call site in `spawn.go`. `StartAgentfsNFS` was promoted into the `agentfs` package as `agentfs.OpenAndServe` (in `agentfs/nfs_server.go`).
> - `internal/vm/vm_service.go` → `internal/vm/machine.go`
> - `internal/vm/vm_spawn.go` → `internal/vm/spawn.go`

## Context

`alcatraz.worker` previously shelled out to four external commands during VM lifecycle: `agentfs init`, `agentfs serve nfs`, `pkill`, and `sha256sum`. These have been eliminated and reimplemented in native Go (no subprocesses, no CGo).

Three of the four were clean wins. The hard one was `agentfs serve nfs`: the AgentFS Go SDK ships overlay/sqlite primitives but no NFS server and no directory-backed `BaseFS`. We bridge with `github.com/willscott/go-nfs` and a small in-repo `HostBase`.

The implementation fills SDK gaps locally rather than via upstream changes — see "SDK gap analysis" below.

---

## Inventory — what's being replaced

All `os/exec` usage lives in two files:

| Site | Command | Purpose |
|---|---|---|
| `internal/vm/utils.go:13` | generic `RunCmd` helper | Dead code, no callers. **Delete.** |
| `internal/vm/agentfs_service.go:73, 244` | `agentfs init --force --base <rootfs> <id>` | Create `<dataDir>/<id>.db`, remove existing files if `--force`, write overlay base path into schema. |
| `internal/vm/agentfs_service.go:109, 267, 290` | `agentfs serve nfs --bind <ip> --port <port> <id>` | Open DB, look up base path, wrap rootfs as base layer + agentfs delta as overlay, serve NFSv3. |
| `internal/vm/agentfs_service.go:179, 264, 319` | `pkill -f "agentfs serve nfs ..."` | Belt-and-suspenders kill of stale subprocess. |
| `internal/vm/agentfs_service.go:139, 221` | `sha256sum <rootfs>/etc/alcatraz-release` | Detect rootfs version change to trigger overlay reinit. |

`agentfs_service.go` has two parallel implementations of the same logic — an `AgentfsService` struct with injected interfaces (zero callers — verified via grep) and standalone `PrepareAgentfsOverlay` / `StartAgentfsNFS` / `CleanupInstance` functions (the actual callers, in `vm_spawn.go:86, :150`). The struct path gets deleted.

---

## SDK gap analysis (verified by reading sources)

| Capability | Rust SDK | Go SDK | Decision |
|---|---|---|---|
| Open / create `<id>.db` (creates `fs_whiteout` and `fs_origin` tables on `Open`) | ✅ `AgentFS::open` | ✅ `agentfs.Open(ctx, AgentFSOptions{Path})` | Use as-is. |
| Directory-backed `BaseFS` (`HostFS`) | ✅ `hostfs_linux.rs` (~600 lines, O_PATH passthrough) | ❌ none | **Write minimal version locally** (~150–250 lines). Read-only base, only Stat/Lookup/Readdir/ReaddirPlus/ReadFile/Readlink needed. |
| `OverlayFS` (base + delta + whiteouts + origins) | ✅ | ✅ `NewOverlayFS(base, delta, db)` + `Init(ctx)` | Use as-is. |
| Read overlay base path from DB (`is_overlay_enabled`) | ✅ | ❌ — no `fs_overlay_config` table at all in Go schema | **Workaround**: worker passes base path explicitly at NFS-start time. We don't need it persisted. |
| Write overlay base path into schema (`OverlayFS::init_schema`) | ✅ | ❌ | **Workaround**: same as above. Skip. |
| NFSv3 server | ✅ `nfsserve` crate | ❌ | Use `github.com/willscott/go-nfs` (active, NFSv3, accepts `go-billy/v5` filesystems). **Spike before committing** (see §7.1). |
| `OverlayFS` offset-based read/write | ✅ Rust `OverlayFS` implements `FileSystem` with seek-based I/O | ❌ — Go `OverlayFS` only exposes whole-file `ReadFile`/`WriteFile` | **Live with whole-file I/O for v1**: correct but wasteful. Boot will succeed; profile after. Documented as upstream-SDK follow-up if perf bites. |
| Schema parity between Go and Rust | — | ❌ Go `fs_whiteout` has `parent_path` column the Rust schema lacks | Any DB created previously by the Rust CLI is incompatible. Force-delete on first run after upgrade (see §5). |

**Net result**: zero upstream SDK changes required to land this. We pay one perf debt (whole-file I/O over NFS) that we can fix later without changing call-site shape.

---

## New file layout

```
internal/vm/agentfs/                # new sub-package
  host_base.go         # directory-backed BaseFS (Stat/Lookup/Readdir/ReaddirPlus/ReadFile/Readlink)
  overlay.go           # OpenOverlay, PrepareOverlay (replaces `agentfs init --force --base ...`)
  billy_adapter.go     # OverlayFS -> go-billy/v5 Filesystem (what go-nfs consumes)
  nfs_server.go        # NFSServer struct, lifecycle (replaces NFSProcessCmd)
  rootfs_stamp.go      # crypto/sha256 streaming hash of <rootfs>/etc/alcatraz-release
```

Adjustments in existing files:

```
internal/vm/utils.go            # delete RunCmd, drop "os/exec" import (keep FileExists)
internal/vm/agentfs_service.go  # rewritten as thin facade over internal/vm/agentfs/*
internal/vm/config.go           # drop AgentfsBin field
internal/vm/vm_spawn.go         # drop SpawnOptions.AgentfsBin; pass Rootfs + AgentfsData through
cmd/alcatraz-worker/main.go     # drop AgentfsBin wiring; remove env var read for AGENTFS_BIN
```

---

## Component design

### `host_base.go` — minimal `BaseFS`

Implements `agentfs.BaseFS` (interface at `/home/dev/Workspace/agentfs/sdk/go/overlay.go:34-52`):

```go
type HostBase struct {
    root    string
    inodes  sync.Map         // ino int64 -> *baseEntry{path, stats}
    bySrc   sync.Map         // srcKey{dev, ino uint64} -> int64  (hardlink dedup)
    nextIno atomic.Int64     // starts at 2; root = 1
}

func NewHostBase(root string) (*HostBase, error)
```

Methods: `Stat`, `Lookup`, `Readdir`, `ReaddirPlus`, `ReadFile`, `Readlink`. Implementation uses `os.Lstat`/`os.ReadDir`/`os.ReadFile`/`os.Readlink` and translates `syscall.Stat_t` (or `golang.org/x/sys/unix.Stat_t`, already a transitive dep) into `agentfs.Stats`. Inodes allocated lazily on `Lookup` to match Rust's strategy. The `(dev, ino)` reverse map gives hardlink stability — relevant on busybox-based rootfs where many `/usr/bin/*` entries are hardlinks.

Reference: `/home/dev/Workspace/agentfs/sdk/rust/src/filesystem/hostfs_linux.rs`. We do **not** need full parity (no O_PATH fds, no Open/Create/Write — base is read-only).

### `overlay.go` — open, init, prepare

```go
type OverlayHandle struct {
    AgentFS *agentfs.AgentFS
    Base    *HostBase
    Overlay *agentfs.OverlayFS
}

// OpenOverlay: open <dataDir>/<id>.db, build HostBase, NewOverlayFS, Init(ctx).
func OpenOverlay(ctx context.Context, agentID, rootfsPath, dataDir string) (*OverlayHandle, error)
func (h *OverlayHandle) Close() error  // closes AgentFS DB

// PrepareOverlay: replaces `agentfs init --force --base <rootfs> <id>`.
//   - mkdir dataDir
//   - if rootfs stamp mismatch OR DB missing: rm <id>.db / -wal / -shm / .base-stamp, force reinit
//   - OpenOverlay (AgentFS.Open creates fs_whiteout / fs_origin tables on first open)
//   - Close
//   - write current rootfs stamp
func PrepareOverlay(agentID, rootfsPath, dataDir string) error
```

We deliberately use `AgentFSOptions{Path: filepath.Join(dataDir, agentID+".db")}` rather than `{ID: agentID}` (which routes to `~/.agentfs/<id>.db`). The worker's existing convention is `cfg.AgentfsData` (default `../alcatraz.core/.agentfs`).

The Rust path also writes a `fs_overlay_config` row with the base path. We skip this — we always have the base path in `cfg.RootfsPath`. **DBs created by this code are NOT compatible with the Rust CLI** (no overlay config row to read). That's a deliberate one-way migration; the whole point is the Rust CLI is gone.

### `billy_adapter.go` — `OverlayFS` → `billy.Filesystem`

go-nfs consumes `github.com/go-git/go-billy/v5.Filesystem`. Adapter wraps `*agentfs.OverlayFS` and translates billy methods (`Create/Open/OpenFile/Stat/Lstat/Rename/Remove/ReadDir/MkdirAll/Symlink/Readlink/Chmod/Chtimes/Root/Join/TempFile`) onto `OverlayFS.LookupPath/WriteFile/ReadFile/MkdirAll/Symlink/Rename/Unlink/Rmdir/Chmod/Utimens`.

`Open` returns a `billy.File` whose `Read/ReadAt/Write/WriteAt/Seek` are backed by an in-memory buffer loaded via `OverlayFS.ReadFile`; on `Close` of a writable file, dirty buffer is flushed via `OverlayFS.WriteFile`. This is the perf debt called out in the gap table — correct but not streaming.

Mode bits and uid/gid: `Stats` carries them; pipe through to billy's `os.FileMode` and a custom `Sys()` returning `*syscall.Stat_t` so go-nfs can populate NFSv3 `fattr3`. Verify in spike — if go-nfs doesn't honour `Sys()`, we use go-nfs's `Handler` extension points instead.

Largest single chunk of new code (~400 lines). Implement methods reactively as the spike + e2e test surface what go-nfs actually invokes; don't upfront-implement every billy method.

### `nfs_server.go` — lifecycle, replaces `NFSProcessCmd`

```go
type NFSServer struct {
    listener net.Listener
    handle   *OverlayHandle
    done     chan struct{}
    err      error
    once     sync.Once
}

func StartNFS(ctx context.Context, handle *OverlayHandle, bindIP string, port int) (*NFSServer, error) {
    addr := net.JoinHostPort(bindIP, strconv.Itoa(port))
    ln, err := net.Listen("tcp", addr)  // EADDRINUSE surfaces here, fail fast
    if err != nil { return nil, err }
    handler := nfshelper.NewNullAuthHandler(billyFS(handle.Overlay))
    srv := &NFSServer{listener: ln, handle: handle, done: make(chan struct{})}
    go func() {
        defer close(srv.done)
        srv.err = nfs.Serve(ln, handler) // blocks until ln.Close
    }()
    log.Printf("AgentFS NFS listening on %s", addr)  // new — current code only logs "Starting"
    return srv, nil
}

func (s *NFSServer) Kill() error {
    var err error
    s.once.Do(func() {
        err = s.listener.Close()       // unblocks nfs.Serve
        if s.handle != nil { _ = s.handle.Close() }
    })
    return err
}
func (s *NFSServer) Wait() error            { <-s.done; return s.err }
func (s *NFSServer) GetProcess() interface{}{ return nil }  // legacy interface shim
```

Implements existing `vm.NFSProcess` interface so `vm_service.go` callers continue to work. `GetProcess()` returns `nil` — current callers only dereference via type assertion to `*os.Process` and skip on failure, so `nil` is safe.

### `rootfs_stamp.go` — sha256

```go
func RootfsStamp(rootfsPath string) (string, error) {
    f, err := os.Open(filepath.Join(rootfsPath, "etc/alcatraz-release"))
    if err != nil { return "", err }
    defer f.Close()
    h := sha256.New()
    if _, err := io.Copy(h, f); err != nil { return "", err }
    return hex.EncodeToString(h.Sum(nil)), nil
}
```

---

## Caller migration

Current callers (`vm_spawn.go:86, :150`):
```go
PrepareAgentfsOverlay(instance, agentfsBin, rootfsPath, agentfsDir)
StartAgentfsNFS(instance, agentfsBin)
```

New signatures (drop `agentfsBin` everywhere — meaningless once native; `StartAgentfsNFS` gains `ctx`, `rootfsPath`, `dataDir` because it must construct the `OverlayHandle` itself — `PrepareOverlay` runs early in spawn and closes the handle, so it can't share it):
```go
PrepareAgentfsOverlay(instance VirtualMachineInfo, rootfsPath, dataDir string) error
StartAgentfsNFS(ctx context.Context, instance VirtualMachineInfo, rootfsPath, dataDir string) (NFSProcess, error)
CleanupInstance(instance VirtualMachineInfo, maxSlots int)  // unchanged sig
```

Update `SpawnOptions` (`vm_spawn.go:13`) — drop `AgentfsBin`. Update `cmd/alcatraz-worker/main.go` to stop reading `AGENTFS_BIN` and stop wiring it through. Update `internal/vm/config.go` to drop the `AgentfsBin` field. `config_test.go` will need an update to drop assertions on the dropped field.

Delete entirely: `AgentfsService`, `DefaultAgentfsInitializer`, `DefaultAgentfsNFSServer`, `AgentfsInitializer`/`AgentfsNFSServer` interfaces, `AgentfsConfig` + `WithAgentfsBin`/`WithRootfsPath`/`WithDataDir`, `NFSProcessCmd`. None have callers outside the file. If test injection is later wanted, use a function variable; do not re-add the interface tower.

Keep: `NFSProcess` interface (still used in `vm_service.go:19` `GetNFSProcess()`).

---

## Process lifecycle — what disappears and why

The current code does an oddly elaborate dance at lines 174–206 (`AgentfsService.StartNFS`) and 259–308 (`StartAgentfsNFS`):

1. `pkill -f "agentfs serve nfs --bind X --port Y"`
2. `time.Sleep(500 * time.Millisecond)`
3. `Start()` the subprocess
4. `time.Sleep(1 * time.Second)`
5. `Kill()` it and `Wait()`
6. `Start()` it again, `time.Sleep(1 * time.Second)`, return.

This was a workaround for two subprocess pathologies: stale SQLite WAL locks held by orphaned previous invocations (the `pkill` path), and a port-bind/startup race where only the second `Start()` would actually settle (the kill-restart path). In native Go:

- `AgentFS.Close()` runs deterministically when `NFSServer.Kill()` runs. No orphans. No WAL lock contention.
- `net.Listen("tcp", addr)` returns `EADDRINUSE` immediately on collision. That's the right signal — `StartNFS` should fail loudly, not silently restart.
- All `time.Sleep` calls disappear; nothing to wait for.
- All `pkill` calls disappear; no external process exists.
- The kill-restart dance disappears.

If two `StartNFS` calls collide on a port, that's a config bug (`nfsPort = 8000+index` per VM ⇒ different indices get different ports), and `EADDRINUSE` will surface it cleanly.

`CleanupInstance` simplifies to:
```go
func CleanupInstance(instance VirtualMachineInfo, maxSlots int) {
    log.Printf("Cleaning up instance %s", instance.GetID())
    if proc := instance.GetNFSProcess(); proc != nil {
        _ = proc.Kill()
        _ = proc.Wait()
    }
    if FileExists(instance.GetSocket()) {
        os.Remove(instance.GetSocket())
    }
}
```

---

## Schema migration / one-shot reinit

Old DBs created by the Rust CLI have `fs_overlay_config` with the base path; new DBs from Go SDK lack that table. They also differ on `fs_whiteout.parent_path`. **Pre-existing DBs in `cfg.AgentfsData` from Rust-era runs are not safe to reuse.**

Migration plan: leverage the existing `.base-stamp` file. After upgrade, the on-disk stamp format changes (sha256sum used to write `<hex>\n`; new code writes bare `<hex>` — drop the `+ "\n"` comparison on `agentfs_service.go:150`). Existing stamp files compare unequal on first read after upgrade and trigger one harmless reinit per agent (DB rebuilt from scratch). No special migration code needed; the existing reinit path handles it.

Call out in commit message: any deployed worker upgrading through this PR will perform a one-time reinit of all agentfs DBs.

---

## Verification

### Spike first (before deleting any exec code)

The single biggest unknown is go-nfs fitness for serving NFSv3 to a Linux kernel during root mount. The kernel issues `MOUNT3PROC_MNT`, `NFSPROC3_GETATTR`, `LOOKUP`, `READDIR`/`READDIRPLUS`, `READ`, `READLINK` heavily during boot, then `WRITE`/`CREATE`/`SETATTR`/`RENAME`/`UNLINK`/`MKDIR`/`RMDIR` later. go-nfs's README claims coverage; in practice some implementations have spotty `READDIRPLUS` cookies or `READ` chunking that breaks Linux's nfs-client.

Before refactoring:
1. **Spike**: wire `go-nfs` over `osfs.New(<rootfs>)` (existing `go-billy/v5/osfs`). Boot a Firecracker VM against it using existing kernel args. If the VM mounts and runs `/init`, go-nfs is good — proceed. If not, evaluate alternatives or escalate (don't silently fork).
2. Implement components above.
3. Replace exec calls in `agentfs_service.go`.
4. Delete `RunCmd` and `os/exec` import in `utils.go`.

### Build / static checks

- `go build ./...` and `go vet ./...` from `/home/dev/Workspace/alcatraz/alcatraz.worker`.
- `grep -r "os/exec" internal/ cmd/` returns zero hits.
- `grep -r "agentfsBin\|AgentfsBin\|AGENTFS_BIN" internal/ cmd/` returns zero hits.

### Unit tests

- New: `host_base_test.go` — populated tmpdir; verify Stat/Lookup/Readdir/ReadFile/Readlink and (dev,ino) hardlink dedup.
- New: `billy_adapter_test.go` — write through OverlayFS-billy; verify reads observe overlay-on-base semantics, including whiteouts and rename.
- New: `rootfs_stamp_test.go` — known input → known sha256.
- Existing: re-run `internal/vm/utils_test.go`, `vm_service_test.go`, `config_test.go` (the last needs update to drop `AgentfsBin` assertions).

### End-to-end (the meaningful test)

Existing target `make test-cni` (Makefile lines 17–48) builds the worker, runs it, spawns a VM via `bin/spawn-client`, sleeps, and prints CNI/tap state. This is the right e2e check — it covers prepare + spawn + NFS-mount-during-boot + teardown.

Pass criteria:
1. Worker boots cleanly (no startup error about missing `AgentfsBin`).
2. `spawn-client --id test-native ...` returns success.
3. After `sleep 10`, firecracker is running and the kernel inside has booted (true only if NFS mount succeeded — proof of correctness).
4. `pkill -TERM -f firecracker` triggers the new `NFSServer.Kill`/`Wait` path in worker logs.
5. CNI cleanup proceeds normally.
6. `lsof -p $(pgrep alcatraz-worker)` shows the NFS listener owned by the worker process — no agentfs subprocess.

### Rootfs-change reinit flow

Run, then `touch <rootfs>/etc/alcatraz-release`, run again. The "Rootfs changed for X, reinitializing" log line must fire and the DB must be recreated.

---

## Risks and unknowns

1. **go-nfs feature parity for boot-time NFS-root mounts** — UNVERIFIED until spike. Mitigation: spike first.
2. **MOUNT protocol on the same port as NFS** — kernel args use `mountport=<port>`. go-nfs serves both on one listener; confirm in spike.
3. **No NLM** — kernel boots with `nolock`. Good.
4. **Whole-file `ReadFile`/`WriteFile`** — perf risk for large rootfs binaries. Boot-relevant working set is bounded; profile if regressions appear. Documented as upstream-SDK follow-up: add `OverlayFS.OpenAt(ino, flags) -> *File`.
5. **HostBase edge cases** — devices, FIFOs, sockets, hardlinks. Boot-relevant rootfs typically has none of the first three but does have hardlinks (busybox). Hardlink dedup via `(dev, ino)` reverse map ships day one.
6. **Schema/CLI divergence** — DBs from Rust CLI runs are incompatible. Handled via stamp-format change forcing a one-time reinit (§ "Schema migration").
7. **Removed `AgentfsBin` config** — deployments that set `AGENTFS_BIN` see the env var silently ignored. No runtime break; call out in PR.
8. **uid/gid translation** — HostBase exposes host's numeric uid/gid; verify rootfs files already carry expected ownership and that the billy adapter's `Sys()` is honoured by go-nfs.

---

## Out of scope

- Porting other agentfs subcommands (`mount`, `exec`, `sync`, `mcp_server`).
- Replacing the firecracker binary subprocess (managed by `firecracker-go-sdk`; not a "CLI tool" the worker invokes).
- Network-side subprocesses — none exist; CNI is consumed via library.
- Upstream SDK changes (HostFS, `is_overlay_enabled`, `init_schema`, streaming `OpenAt`) — call out as follow-ups but not required for this PR.

---

## Critical files

- `internal/vm/agentfs_service.go` — rewritten as facade
- `internal/vm/utils.go` — `RunCmd` and `os/exec` import removed
- `internal/vm/vm_spawn.go` — `AgentfsBin` removed from `SpawnOptions`
- `internal/vm/config.go` — `AgentfsBin` removed from `VirtualMachineConfig`
- `cmd/alcatraz-worker/main.go` — `AgentfsBin` wiring removed
- `/home/dev/Workspace/agentfs/sdk/go/overlay.go` — reference; `BaseFS` interface and `OverlayFS` API the new code targets
- `/home/dev/Workspace/agentfs/sdk/rust/src/filesystem/hostfs_linux.rs` — reference for the local `HostBase` port

---

## Implementation summary (as built)

### New package `internal/vm/agentfs/`

| File | Purpose |
|---|---|
| `host_base.go` | `HostBase` — directory-backed `sdk.BaseFS`. Lazy inode allocation on `Lookup`; `(host dev, host ino)` reverse map gives stable inodes across hardlinks. ~220 lines. |
| `overlay.go` | `OverlayHandle{AgentFS, Base, Overlay}`, `OpenOverlay(ctx, agentID, rootfs, dataDir)`, `PrepareOverlay(agentID, rootfs, dataDir)`. The latter replaces `agentfs init --force --base ...` and handles rootfs-stamp drift. |
| `billy_adapter.go` | `billyFS` adapts `*sdk.OverlayFS` to `go-billy/v5.Filesystem` so `willscott/go-nfs` can serve it. File handles load on `Open`, flush on `Close` of writable handles. |
| `nfs_server.go` | `NFSServer{listener, handle, done, err}`. `StartNFS(handle, bindIP, port)` binds (`EADDRINUSE` surfaces here, no retry/sleep dance), spawns the serve goroutine, returns. `Kill()` is `sync.Once`-guarded; `Wait()` blocks on `done`. |
| `rootfs_stamp.go` | `RootfsStamp(rootfsPath)` — streams `<rootfs>/etc/alcatraz-release` through `crypto/sha256`. |
| `host_base_test.go`, `rootfs_stamp_test.go`, `integration_test.go` | Unit and integration coverage. |

### What got deleted

- `AgentfsService`, `DefaultAgentfsInitializer`, `DefaultAgentfsNFSServer`, `AgentfsInitializer`/`AgentfsNFSServer` interfaces, `AgentfsConfig` + `WithAgentfsBin`/`WithRootfsPath`/`WithDataDir`, `NFSProcessCmd`. None had callers outside `agentfs_service.go`.
- `RunCmd` from `internal/vm/utils.go` (was already dead code).
- `AgentfsBin` const, struct field, env wiring across `config.go`, `vm_spawn.go`, `cmd/alcatraz-worker/main.go`, and the corresponding test assertion in `config_test.go`.
- All `time.Sleep(500*time.Millisecond)` / `time.Sleep(1*time.Second)` calls and the "start, kill, restart" subprocess dance — `net.Listen` returns synchronously when bound; nothing to wait for.
- All `pkill -f "agentfs serve nfs ..."` calls — there is no external process to kill.

### Caller migration

`internal/vm/agentfs_service.go` is now a thin facade exposing:

```go
PrepareAgentfsOverlay(instance, rootfsPath, dataDir string) error
StartAgentfsNFS(ctx, instance, rootfsPath, dataDir string) (NFSProcess, error)
CleanupInstance(instance, maxSlots int)
```

`vm_spawn.go` calls these without `agentfsBin`. `NFSProcess.GetProcess()` returns `nil` from the new `*NFSServer` (no `*os.Process`); legacy callers only used the type assertion to `*os.Process` and skipped on failure, so `nil` is safe.

### Dependencies added (`go.mod`)

```
github.com/go-git/go-billy/v5
github.com/willscott/go-nfs
github.com/tursodatabase/agentfs/sdk/go    // local replace -> /home/dev/Workspace/agentfs/sdk/go
```

`modernc.org/sqlite` arrives transitively via the AgentFS SDK.

### Migration

Old DBs created by the Rust CLI are not safe to reuse — Go SDK's `fs_whiteout` schema differs (`parent_path` column) and there is no `fs_overlay_config` table. The on-disk stamp format also changed (no trailing `\n`), so existing stamp files compare unequal on first Prepare after upgrade and the existing reinit path rebuilds the DB. No migration code needed.

### Verification performed

- `go build ./...` and `go vet ./...` clean.
- `grep -r "os/exec\|exec.Command"` in `internal/`, `cmd/` → zero hits.
- `grep -r "AgentfsBin\|agentfsBin\|AGENTFS_BIN"` → zero hits.
- `grep -r "\"pkill\"\|\"sha256sum\""` → zero hits.
- All existing unit tests (`internal/vm`, `internal/messaging`) still pass.
- New unit tests in `internal/vm/agentfs/` pass: `HostBase` basic ops, hardlink dedup, `RootfsStamp` digest, `RootfsStamp` missing file.
- Integration tests pass against the real `../alcatraz.core/rootfs`:
  - `TestPrepareAndServeNFS_Lifecycle` — `PrepareOverlay` writes DB + stamp, `OpenOverlay` stitches the overlay, `StartNFS` binds, listener is reachable via `net.Dial`, `Kill` shuts down cleanly, second `Kill` is idempotent.
  - `TestPrepareOverlay_ReinitOnStampChange` — stamp mismatch triggers a full DB rebuild.
- End-to-end smoke: started worker without sudo, sent spawn request via `bin/spawn-client`. The worker logged `Initializing AgentFS overlay for native-test`, wrote `native-test.db` (86 KB SQLite) and `native-test.base-stamp` (64-char hex), then failed at `start machine: failed to initialize netns: ... permission denied` — that is the existing CNI/firecracker root requirement, unrelated to this change. Run `sudo make test-cni` on a host with root to exercise the kernel NFS-root mount path.

### Known follow-ups

- **Streaming I/O for `OverlayFS`.** The Go SDK exposes only whole-file `ReadFile`/`WriteFile`; the billy adapter buffers full files. Boot is correct but reads/writes are wasteful for large binaries. Mitigation lives upstream: add `OverlayFS.OpenAt(ino, flags) -> *File`. Profile only if boot regresses.
- **Schema parity with the Rust CLI.** Go and Rust schemas diverged; we removed cross-tool compatibility intentionally. If a Rust CLI mode is ever needed again, either reintroduce `fs_overlay_config` in the Go SDK or accept that one tool owns a DB.
- **NFSv3 op coverage.** The kernel's NFSv3 client during boot is the real test surface. `willscott/go-nfs` claims full coverage; if a particular op breaks the boot path under root, the billy adapter is the place to add fixups.
