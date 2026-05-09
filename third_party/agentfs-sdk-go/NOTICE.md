# NOTICE

## Upstream attribution

This directory is a vendored copy of the Go SDK from:

- **Project**: AgentFS
- **Repository**: https://github.com/tursodatabase/agentfs
- **Subdirectory**: `sdk/go/`
- **Version**: tag `v0.6.4` (commit `3a5ed2b`)
- **Copyright**: © Turso, contributors. See upstream repository for the full list.
- **License**: MIT (per the upstream README; canonical text at https://github.com/tursodatabase/agentfs/blob/main/LICENSE.md)

All files in this directory other than the following originate verbatim from the upstream snapshot above:

- `UPSTREAM.md` — Alcatraz-authored provenance and bump procedure
- `NOTICE.md` — this file
- `patches/` — Alcatraz-authored patches against the upstream snapshot

The patches in `patches/` are released under the MIT License, consistent with both upstream AgentFS and Alcatraz itself (see [`/LICENSE`](../../LICENSE)).

## Transitively bundled licenses

Upstream AgentFS itself bundles small portions of two third-party Rust crates (`fuser`, `nfsserve`) under their own licenses. Those are included here for completeness; they are present in the upstream repo at `licenses/LICENSE-fuser.md` and `licenses/LICENSE-nfsserve.md`. The Go SDK in this directory does not consume them at runtime — they apply to the broader AgentFS project — but vendoring conventions ask that we surface them when we redistribute upstream sources.

If you redistribute Alcatraz, you redistribute this vendored module too, and these notices travel with it.
