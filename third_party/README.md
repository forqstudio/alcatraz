# third_party

This directory holds **vendored** copies of upstream code that Alcatraz depends on but does not author. "Vendored" means the source is checked into this repo verbatim (with a small, documented patch set) and resolved locally instead of being fetched from the public module registry at build time.

Each subdirectory:
- pins a specific upstream commit/tag (recorded in its `UPSTREAM.md`),
- carries any divergence as files in `patches/` so the diff against upstream is auditable,
- preserves the original module path and license,
- and is wired into a consumer via a `replace` directive in that consumer's `go.mod` (the import path stays canonical; only resolution is redirected).

## What's here

| Directory | Upstream | Tracked at | Why vendored |
|---|---|---|---|
| [`agentfs-sdk-go/`](./agentfs-sdk-go/) | https://github.com/tursodatabase/agentfs (`sdk/go/`) | tag `v0.6.4` (commit `3a5ed2b`) | `alcatraz.worker` requires an `OverlayFS.Mkdir` copy-up fix that does not exist in any upstream release. Without the fix, the worker's pre-boot file plant of `/etc/ssh/auth_principals/al` and `/etc/ssh/trusted_user_ca_keys` fails with ENOENT, and SSH cert auth silently breaks for every spawn. See [`agentfs-sdk-go/UPSTREAM.md`](./agentfs-sdk-go/UPSTREAM.md). |

## Why vendor at all

Vendoring is a heavier choice than pinning to a published version, and we don't take it lightly. Each module here lives under one of these conditions:

1. **Required behaviour isn't in any released version yet** *and* upstreaming on Alcatraz's timeline isn't feasible (we're not maintainers of the upstream project).
2. **A bug fix or behaviour change is needed** that upstream is unlikely to accept, or that we want under our control to roll forward at our own pace.

The alternatives we rejected and why:
- **Pin to a published tag**: would lock in a known-broken version (see `agentfs-sdk-go/UPSTREAM.md` for the concrete failure mode).
- **Local-path `replace` to a sibling git checkout**: leaks one developer's filesystem layout into committed code. Anyone else (CI, devcontainer, a fresh clone) cannot build. This is the situation `third_party/` exists to escape.
- **Fork to our own GitHub org**: more infrastructure to maintain (a separate repo, release cadence, Dependabot) for a single small patch consumed by one component.

## How vendoring is wired up

Inside the vendored module, the `go.mod` keeps its **canonical** module path (e.g. `module github.com/tursodatabase/agentfs/sdk/go`). Worker code imports it under that canonical path; nothing in `alcatraz.worker/` knows or cares about `third_party/`.

The redirect lives in the consumer's `go.mod`:

```
replace github.com/tursodatabase/agentfs/sdk/go => ../third_party/agentfs-sdk-go
```

A relative path travels in git, works in CI, works in a fresh clone, and works inside the devcontainer — none of which is true for an absolute or sibling-checkout path.

## Updating a vendored module

Each `UPSTREAM.md` contains a step-by-step bump procedure tailored to that module. The general shape:

1. Check out the new upstream tag in a working clone of the upstream repo.
2. `rsync -a --delete --exclude='.git/' …` over the vendored directory, preserving in-repo additions (`patches/`, `UPSTREAM.md`, this dir's `README.md`).
3. Re-apply the patch series in `patches/` in numeric order. If a patch no longer applies cleanly, either upstream has fixed the issue (drop the patch and update the table above) or rebase by hand and regenerate the `.patch` file.
4. From the consumer module: `go mod tidy`, `go build ./...`, `go test ./...`.
5. Update the "Tracked at" entry in the table above and in the module's `UPSTREAM.md`.

## Licensing

Every vendored module preserves its upstream license. Alcatraz is MIT-licensed (see [`/LICENSE`](../LICENSE)); a vendored module is only acceptable here if its license is MIT-compatible. Each subdirectory carries a `NOTICE.md` (or equivalent) recording the upstream license posture and any attribution required.
