# Modernize `.devcontainer/` for Alcatraz

## Context

`.devcontainer/devcontainer.json` and `.devcontainer/Dockerfile` haven't been touched since the initial commit (`0e0e89a`), when the repo was a single project called `firecracker-agentfs`. The repo has since become the multi-component Alcatraz platform (.NET 8 API + CLI, two Go 1.25 services, kernel/rootfs builders), and the dev container no longer matches: the workspace path still says `firecracker-agentfs`, the only language toolchain installed is Rust, there are no C# or Go extensions, and the Dockerfile installs runtime binaries (firecracker, opencode) we no longer want inside it.

Goal: a teammate can clone the repo, "Reopen in Container", and have everything they need to **edit and build** all five components within seconds — without privileged mode, KVM passthrough, or in-container Docker. Heavy/privileged tasks (kernel build, rootfs build, `docker compose up`, the worker) stay on the host, exactly as the README's Quick start describes them today.

## Approach

Decisions made up front (so the plan stays focused):

- **Scope: code + build only.** Drop `--privileged`, `--cap-add=SYS_ADMIN`, `--device=/dev/kvm`. No Docker-in-Docker. No firecracker binary, no opencode, no agentfs runtime inside the container.
- **Custom Dockerfile, no devcontainer Features.** Single file, easy to read and modify.
- **`postCreateCommand` runs only the cheap stuff** — `dotnet restore` for both solutions, `go mod download` for both modules, and prints a banner that points at the host-side commands for the slow/privileged steps.

## Files to modify

| File | Change |
|---|---|
| `.devcontainer/Dockerfile` | Full rewrite (§1). |
| `.devcontainer/devcontainer.json` | Rewrite name, paths, runArgs, extensions, postCreate, env (§2). |
| `.devcontainer/postCreate.sh` | New helper script (§3). `chmod +x` and `git update-index --chmod=+x`. |
| `.dockerignore` | Append `.containers/`, `obj/`, `**/bin/` to keep the build context small. |

## 1. Dockerfile rewrite

Base on `mcr.microsoft.com/devcontainers/base:ubuntu-24.04` — already ships the `vscode` user at uid/gid 1000 (matches the host `dev` user, so bind-mount perms line up), `sudo`, `git`, locales.

apt deps (single layer, `rm -rf /var/lib/apt/lists/*` after):

```
build-essential bc bison flex cpio kmod libelf-dev libssl-dev rsync tar xz-utils
ca-certificates curl wget gpg debootstrap shellcheck
git sudo jq openssh-client iproute2 iputils-ping unzip make pkg-config
dotnet-sdk-8.0
```

Notes:

- `dotnet-sdk-8.0` is in noble's default repo — no Microsoft package repo needed. `global.json` pins `8.0.0` `latestFeature`, satisfied by any 8.0.x.
- The kernel/rootfs build prerequisites (`debootstrap`, `bc`, `bison`, `flex`, `kmod`, `libelf-dev`, `libssl-dev`, `cpio`, `xz-utils`, `rsync`) are installed *anyway* — so a dev *can* run those inside if they `sudo` in; we just don't auto-run them.
- `iptables` deliberately omitted — only `run.sh`/`firecracker.sh` NAT setup needs it, and that's a host-only path.
- `llvm-dev`, `libkmod-dev` from the old Dockerfile dropped — kernel build uses kmod userspace, not headers; nothing else links libllvm.
- Rust toolchain dropped — `cargo install agentfs` is gone, and `build-rootfs.sh` installs Rust *inside the chroot* via `rustup`, never on the host.

Go 1.25 install (not in noble apt; tarball is the canonical path):

```dockerfile
ARG GO_VERSION=1.25.3
RUN curl -fsSL https://go.dev/dl/go${GO_VERSION}.linux-amd64.tar.gz \
    | tar -C /usr/local -xz \
 && printf 'export PATH=$PATH:/usr/local/go/bin:$HOME/go/bin\nexport GOPATH=$HOME/go\n' \
    > /etc/profile.d/go.sh
ENV PATH=/usr/local/go/bin:/home/vscode/go/bin:${PATH} \
    GOPATH=/home/vscode/go
```

Drop entirely from the old Dockerfile: firecracker tarball install, `cargo install agentfs`, opencode curl-pipe-sh, all `*_PATH` / `*_VERSION` env for those, the trailing `COPY --chown=...` of the workspace (devcontainer.json bind-mounts it).

Update `LABEL description` and `LABEL maintainer` from `firecracker-agentfs` to `alcatraz`.

## 2. `devcontainer.json` rewrite

```jsonc
{
  "name": "alcatraz",
  "build": { "context": "..", "dockerfile": ".devcontainer/Dockerfile" },
  "remoteUser": "vscode",
  "workspaceFolder": "/workspaces/alcatraz",
  "workspaceMount": "source=${localWorkspaceFolder},target=/workspaces/alcatraz,type=bind,consistency=cached",
  "customizations": {
    "vscode": {
      "extensions": [
        "ms-dotnettools.csharp",
        "ms-dotnettools.csdevkit",
        "golang.go",
        "ms-azuretools.vscode-docker",
        "ms-vscode.makefile-tools",
        "redhat.vscode-yaml",
        "esbenp.prettier-vscode",
        "davidanson.vscode-markdownlint",
        "timonwong.shellcheck"
      ],
      "settings": {
        "terminal.integrated.defaultProfile.linux": "bash",
        "files.eol": "\n",
        "files.trimTrailingWhitespace": true,
        "editor.formatOnSave": true,
        "[shellscript]": { "editor.defaultFormatter": "esbenp.prettier-vscode" },
        "go.toolsManagement.autoUpdate": true
      }
    }
  },
  "postCreateCommand": "bash .devcontainer/postCreate.sh",
  "containerEnv": {
    "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
    "DOTNET_NOLOGO": "1"
  }
}
```

Removed from the old config: `runArgs` (KVM/privileged/SYS_ADMIN/memory/cpus), `forwardPorts` (empty list), all firecracker/agentfs/opencode env, the `ms-python.python` and `ms-vscode.shell-syntax` extensions.

`build.context` flips from `"."` to `".."` because the Dockerfile no longer needs to COPY the repo — the workspace is bind-mounted at runtime. The wider context is harmless and matches typical devcontainer convention so future ADD/COPY would resolve correctly.

## 3. `.devcontainer/postCreate.sh`

```bash
#!/usr/bin/env bash
set -euo pipefail
cd /workspaces/alcatraz

echo "==> dotnet restore (api)"
dotnet restore alcatraz.api/Alcatraz.sln
echo "==> dotnet restore (cli)"
dotnet restore alcatraz.cli/Alcatraz.Cli.sln
echo "==> go mod download (worker)"
(cd alcatraz.worker && go mod download)
echo "==> go mod download (routes)"
(cd alcatraz.routes && go mod download)

cat <<BANNER

Alcatraz devcontainer ready.
  dotnet : $(dotnet --version)
  go     : $(go version | awk '{print $3}')

Heavy / privileged steps still run on the host (need KVM, sudo, Docker):
  sudo -E ./alcatraz.core/build-kernel.sh        # ~10 min, one-time
  sudo -E ./alcatraz.core/build-rootfs.sh        # ~10 min, one-time
  docker compose up -d --build                   # control plane
  sudo alcatraz.worker/scripts/sync-ca-pubkey.sh # after each host reboot
  sudo -E ./alcatraz.worker/bin/alcatraz-worker  # worker (host-run)

See README.md "Quick start" for the full walkthrough.
BANNER
```

## 4. `.dockerignore` tweak

Append to the existing file (which already excludes `linux-amazon`, `rootfs`, `vmlinux`, `bin`, `.git`, `.agentfs`):

```
.containers
obj
**/bin
```

`.containers/database/` is `root`-owned (Postgres data dir from `docker-compose.yml`) and would otherwise blow up image build context size.

## What we reuse / what already exists

- `mcr.microsoft.com/devcontainers/base:ubuntu-24.04` already provides the `vscode` user, `sudo`, common-utils — saves ~20 lines vs. the current handcrafted user setup.
- `.dockerignore` already excludes most build artefacts; we only add three lines.
- `global.json` already pins .NET 8 — no change needed there.
- The README's Quick start (`README.md` §Quick start) is the source of truth for host-side commands; the postCreate banner just points at it rather than duplicating.

## Verification

After the changes are in:

1. From the host, in VS Code: `Dev Containers: Rebuild and Reopen in Container`. First build runs apt install + Go tarball download + dotnet SDK install (~3–5 min on a warm cache).
2. `postCreateCommand` should print restore output for both .NET solutions and both Go modules, then the banner.
3. Inside the container, smoke-test the toolchains:
   ```bash
   dotnet --version          # 8.0.x
   go version                # go1.25.3 linux/amd64
   dotnet build alcatraz.api/Alcatraz.sln
   dotnet build alcatraz.cli/Alcatraz.Cli.sln
   make -C alcatraz.worker build
   make -C alcatraz.routes build
   make -C alcatraz.worker test
   make -C alcatraz.routes test
   ```
   All should succeed. The two `make build` produce `bin/alcatraz-worker`, `bin/spawn-client`, `bin/alcatraz-routes`.
4. Confirm sandbox runtime steps still error out cleanly (KVM passthrough is removed deliberately):
   ```bash
   ls /dev/kvm                                   # → No such file or directory
   sudo -E ./alcatraz.core/run.sh                # → fails on /dev/kvm; expected
   ```
5. From the host (NOT the container), the existing Quick start in `README.md` still works end-to-end — nothing about the host workflow changes.

## Gotchas

- Workspace path moves from `/home/vscode/workspaces/firecracker-agentfs` → `/workspaces/alcatraz`. No code in the repo hard-codes the container path. (`alcatraz.worker/Makefile`'s `test-cni` target uses a host path, not container path — already wouldn't work inside.)
- Dropping `--privileged` removes `/dev/kvm`, by design. Anyone who tries to run firecracker inside will get a clean error instead of silent corruption.
- `build-rootfs.sh` does `sudo mount --bind /dev /proc /sys /run` inside its chroot — that's why we don't auto-run it; it needs host privileges that "code + build only" deliberately rejects.
- Host `dev` uid/gid is 1000:1000 and the devcontainers/base `vscode` user is 1000:1000 — mounts line up. Teammates whose host uid differs can re-build with `--build-arg USER_UID=...` per the devcontainers/base docs.
