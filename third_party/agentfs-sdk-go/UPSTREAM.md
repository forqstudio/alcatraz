# agentfs-sdk-go (vendored)

A copy of the Go SDK from https://github.com/tursodatabase/agentfs/tree/main/sdk/go, frozen at a specific upstream commit and patched locally. See the parent [`third_party/README.md`](../README.md) for the general vendoring conventions.

## Source

- **Repo**: https://github.com/tursodatabase/agentfs
- **Subdirectory**: `sdk/go/`
- **Tracked at**: tag `v0.6.4` (commit `3a5ed2b`, dated 2026-05-09)

The Go module path is preserved verbatim:

```
module github.com/tursodatabase/agentfs/sdk/go    // (in this directory's go.mod)
```

`alcatraz.worker/go.mod` redirects resolution here:

```
replace github.com/tursodatabase/agentfs/sdk/go => ../third_party/agentfs-sdk-go
```

Worker code imports the canonical path (`import sdk "github.com/tursodatabase/agentfs/sdk/go"`) — it doesn't reference `third_party/` directly.

## Why this exists

`alcatraz.worker` uses the AgentFS Go SDK to manage per-sandbox filesystem overlays (a read-only base layer = the rootfs, plus a writable per-VM SQLite delta — see `alcatraz.worker/README.md` line 14 onward). Two of the worker's responsibilities depend on overlay-aware directory creation:

- **Pre-boot file plant.** Before each Firecracker VM starts, `OverlayHandle.WriteFile` (`alcatraz.worker/internal/vm/agentfs/overlay.go:64`) writes `/etc/ssh/auth_principals/al` (the sandbox UUID) and `/etc/ssh/trusted_user_ca_keys` (the API's CA pubkey) into the overlay. `sshd` validates customer certs against these files; without them, every `alcatraz ssh` fails with `Permission denied (publickey)`.
- **Customer writes inside the VM.** Anything the SSH user creates under base-layer directories (e.g. `mkdir /workspace/foo` when `/workspace` exists in the base rootfs) goes through the same overlay code path via the worker's in-process NFSv3 server.

Both paths trip a bug in upstream `OverlayFS.Mkdir` and `OverlayFS.MkdirAll`: when the parent of the target path exists **only in the base layer**, the SDK calls `delta.Mkdir(...)` directly without first materialising the parent in the delta layer. SQLite's delta filesystem returns `ENOENT` because, from its point of view, the parent directory does not exist.

This is exactly the case the worker hits the moment it tries to mkdir `/etc/ssh/auth_principals` (parent `/etc/ssh/` is in the base rootfs only). The whole spawn pipeline fails — and because the failure is on the pre-boot plant rather than a runtime check, the symptom downstream is the worst kind: the VM looks healthy, the worker reports `vm.ready`, the route table is updated, and only the customer's actual `ssh` command fails with a generic public-key error.

The fix copies the missing parent into the delta (via the SDK's private `ensureParentDirs` helper) before each `delta.Mkdir`. It's a 14-line change to one file. We are not maintainers of `tursodatabase/agentfs`, so we cannot land this upstream and cut a release on Alcatraz's timeline; vendoring is the steady state, not a bridge.

There is no public-API workaround. `ensureParentDirs` and `copyUp` are unexported. `OverlayFS.WriteFile` transitively calls the buggy `MkdirAll`. The worker's import surface (`OverlayFS`, `BaseFS`, `RootIno`, `AgentFS`, `AgentFSOptions`) doesn't expose anything that bypasses the broken path.

## Patches applied on top of upstream

Applied in numeric order from `patches/`:

| File | Subject | Lines | Touches |
|---|---|---|---|
| [`0001-overlay-mkdir-ensure-parent-dirs.patch`](./patches/0001-overlay-mkdir-ensure-parent-dirs.patch) | `OverlayFS.Mkdir` / `MkdirAll`: copy-up parent dirs from base to delta before creating in delta. | 14 | `overlay.go` only |

To verify the vendored tree exactly equals upstream-v0.6.4 + this patch series, from the alcatraz repo root:

```bash
cd third_party/agentfs-sdk-go
git apply --reverse --check patches/0001-overlay-mkdir-ensure-parent-dirs.patch
# (no output = clean)
```

## Bumping to a newer upstream version

When upstream cuts a release worth tracking:

1. Pull the new tag in a working clone:
   ```bash
   git -C ~/Workspace/agentfs fetch origin --tags
   git -C ~/Workspace/agentfs checkout <new-tag>
   ```
2. Replace the vendored tree (preserves `patches/`, `UPSTREAM.md`, `NOTICE.md`):
   ```bash
   rsync -a --delete \
     --exclude='.git/' --exclude='.gitignore' \
     --exclude='patches/' --exclude='UPSTREAM.md' --exclude='NOTICE.md' \
     ~/Workspace/agentfs/sdk/go/ third_party/agentfs-sdk-go/
   ```
3. Re-apply the patch series:
   ```bash
   for p in third_party/agentfs-sdk-go/patches/*.patch; do
     git apply --directory=third_party/agentfs-sdk-go "$p" || break
   done
   ```
   If a patch fails: either upstream has merged an equivalent fix (delete the patch file and the row in the table above), or the surrounding code drifted (rebase the patch by hand, regenerate the `.patch` body).
4. Refresh `go.sum`:
   ```bash
   (cd alcatraz.worker && go mod tidy)
   ```
5. Update the **Tracked at** line in this file and the entry in [`third_party/README.md`](../README.md).
6. Run the worker's verification: `go build ./...`, `go test ./...`, then a full-stack spawn smoke test (the spawn pipeline exercises `OverlayHandle.WriteFile → MkdirAll(/etc/ssh/auth_principals)` — a successful `alcatraz ssh` proves the patch is wired in).

## Licensing

Upstream `tursodatabase/agentfs` is MIT-licensed (declared in their README; the canonical `LICENSE.md` lives at https://github.com/tursodatabase/agentfs/blob/main/LICENSE.md). Alcatraz itself is MIT-licensed. The patch in `patches/` is also released under MIT, consistent with both. See [`NOTICE.md`](./NOTICE.md) for full attribution and the upstream-vendored licenses (fuser, nfsserve) carried alongside.
