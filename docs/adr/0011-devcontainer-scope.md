# ADR-0011: Devcontainer scope = code + build only

- **Status:** Accepted
- **Date:** 2026-05-10
- **Related:** [`0010-single-root-docker-compose.md`](0010-single-root-docker-compose.md)

## Context

The natural impulse with a multi-language repo is "ship a devcontainer that does everything" — toolchains, dependencies, *and* the runtime stack. But Alcatraz's runtime stack genuinely needs host privileges that a devcontainer can't reasonably grant:

- `docker compose up` needs a working Docker daemon (Docker-in-Docker is a foot-gun, especially with the volume + network setup we use).
- `alcatraz.worker` needs KVM passthrough, root, and a host-side bridge namespace — it spawns Firecracker VMs and configures CNI.
- The kernel build and rootfs build pull GBs of artefacts and write them to host-mapped paths.

Trying to host any of these inside the devcontainer means either dropping `--privileged` (broken) or shipping a privileged container (foot-gun: the devcontainer image becomes a privileged container that any teammate runs by default).

What teammates actually want from a devcontainer is the *cheap* stuff: language SDKs, code intelligence, fast `restore` / `mod download`, consistent linter/formatter versions. Drawing the scope at "code + build" matches that demand and avoids the privilege trap.

## Decision

The devcontainer covers **code + build only**. Heavy and privileged tasks stay on the host.

`.devcontainer/` builds a non-privileged Ubuntu 24.04 image with:

- `dotnet-sdk-8.0`, Go 1.25.
- Kernel and rootfs build prerequisites (compilers, headers, libelf, etc.) — present so `build-rootfs.sh` *can* compile inside the container if a teammate explicitly chooses to, but the script still fails fast for tasks that need root.
- Standard CLI tooling: `shellcheck`, `jq`.
- **No KVM passthrough. No Docker-in-Docker. No firecracker binary.**

`postCreate.sh` runs the cheap stuff (`dotnet restore` × 2, `go mod download` × 2) and prints a banner pointing at the host-side commands for kernel build, rootfs build, `docker compose up`, and the worker — all of which need host privileges and stay on the host. Heavy and privileged tasks deliberately fail inside the container with a clean error.

## Consequences

### Positive

- **Devcontainer is small and fast to build.** No KVM detection, no docker-in-docker setup, no firecracker download.
- **No surprise privilege escalation.** The devcontainer image is unprivileged; no foot-gun where a teammate accidentally runs a privileged container.
- **Onboarding cost is honest.** "VS Code's devcontainer for code; host commands for everything else" is a readable contract.
- **Host workflow stays simple.** The host needs Docker, KVM, and the standard build deps — nothing the devcontainer is fighting against.

### Negative

- **Two environments to onboard against.** Teammates need both the devcontainer (for code intelligence) *and* a working host (for the runtime). A pure-cloud workstation isn't enough.
- **The "fail with a clean error" contract is fragile.** Each host-only task needs a guard; missing a guard means a teammate gets a confusing failure deep inside a build script.
- **No CI parity by default.** CI runs on the host shape, not the devcontainer shape — drift between them is possible. Mitigation: keep the toolchain versions in the devcontainer and CI image in lockstep.
