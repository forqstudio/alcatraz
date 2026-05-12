# Alcatraz.Core

This project runs a Firecracker microVM whose root filesystem is served from an AgentFS overlay over NFS.

This repository contains the build and launch scripts only. Generated artifacts such as `alcatraz.core/rootfs/`, `alcatraz.core/linux-amazon/`, `alcatraz.core/.agentfs/`, `alcatraz.core/vmlinux`, and temporary `vm_config.*.json` files are intentionally excluded from version control.

## The Stack

- Guest rootfs: Ubuntu 24.04 `noble`, built with `debootstrap`
- Guest kernel: Amazon Linux microVM kernel tag `microvm-kernel-6.1.167-27.319.amzn2023`
- Firecracker target: `v1.15.1`
- AgentFS target: `v0.6.4`
- Pi: `@mariozechner/pi-coding-agent@latest` from npm

## How It Works

1. `build-kernel.sh` clones the Amazon Linux kernel repo and builds `vmlinux`.
2. `build-rootfs.sh` bootstraps an Ubuntu rootfs and installs developer tooling into it.
3. `firecracker.sh` creates or reuses an AgentFS overlay in `.agentfs/<agent-id>.db`.
4. `firecracker.sh` exports that overlay over NFS using the host `agentfs` binary.
5. Firecracker boots the VM with `root=/dev/nfs`, so the guest rootfs is the AgentFS-backed overlay.
6. The launcher sets up a TAP device plus outbound NAT so the guest can reach the internet.

The practical effect is:
- the guest starts from a reusable Ubuntu base image
- all guest filesystem changes are persisted into the AgentFS SQLite DB
- you can inspect changes later with `agentfs diff <agent-id>`

## Host Requirements

You need these on the host:
- Ubuntu or another Linux host with `sudo`
- KVM / Firecracker support
- `agentfs` installed on the host and available on `PATH`
- package install permissions for `apt`, `debootstrap`, TAP setup, and `iptables`

`build-rootfs.sh`, `build-kernel.sh`, and `firecracker.sh` all require `sudo`. Always run them via `sudo -E` to preserve `$HOME`.

The base rootfs is intentionally cert-only — no host SSH pubkeys are baked in at build time, so customer VMs spawned by `alcatraz.worker` get only the per-sandbox cert pipeline (`TrustedUserCAKeys` + `AuthorizedPrincipalsFile`). The dev-convenience `run.sh` / `firecracker.sh` path injects `authorized_keys` into the per-VM AgentFS overlay at boot (scoped to that one VM, never on disk in the shared rootfs). Set `HOST_SSH_DIR=` (empty) on a `firecracker.sh` invocation to opt out and use cert-only auth even for dev VMs.

The launcher expects:
- host uplink interface detectable from `ip route show default`
- `iptables` available for NAT

## Host/Guest Boundary

Host-side:
- Firecracker process
- AgentFS NFS server
- TAP device and NAT rules
- AgentFS overlay DBs in `.agentfs/`

Guest-side:
- Ubuntu userspace
- developer tools
- `/workspace`
- `/init` runs `exec /usr/bin/tini -- /usr/sbin/sshd -D ...` after mounts and host-key generation. `tini` is PID 1 — it forwards signals (so the worker's `StopVMM` shuts the VM down cleanly via `SIGTERM`) and reaps zombies (orphaned grandchildren of customer SSH sessions don't accumulate). `sshd` runs in the foreground; if it ever exits, `tini` exits with its code, the kernel panics (`panic=1 reboot=k`), and the worker observes the exit and publishes `vm.destroyed`. The serial console is not user-facing — customer access is exclusively via SSH.
- `sshd` configured for cert-based auth: `TrustedUserCAKeys /run/ssh-config/trusted_user_ca_keys` + `AuthorizedPrincipalsFile /run/ssh-config/auth_principals/%u`. Both files are written into tmpfs by `/init` from kernel cmdline args (`alcatraz.agent_id=<uuid>` and `alcatraz.ca_pubkey=<base64>`) that `alcatraz.worker` appends at spawn. Password auth is disabled. The base rootfs has **no `authorized_keys`** — customer VMs are exclusively cert-authenticated. The dev `firecracker.sh` path doesn't pass those cmdline args and plants `authorized_keys` into the per-dev-VM AgentFS overlay instead; sshd logs a one-line "Could not open …/trusted_user_ca_keys" warning on the dev path, which is expected.
- outbound access to GitHub, npm, and Ubuntu mirrors through host NAT

## Guest Defaults

- hostname: `alcatraz`
- user: `al`
- password: `dev` by default, configurable with `VM_USER_PASSWORD` (only used by `chpasswd` at build; `sshd` rejects password auth — keep this around for serial-console fallback)
- sudo: passwordless
- working directory after login: `/workspace`
- default VM size: `4` vCPU, `8192` MiB RAM
- default network:
  - host TAP device: `fc-tap0`
  - host TAP IP: `172.16.0.1`
  - guest IP: `172.16.0.2`
  - subnet: `172.16.0.0/24`
- default AgentFS NFS port: `11111`

## Guest Tooling

Installed APT packages:

- **apt-utils** - APT package management utilities
- **apt-transport-https** - Download packages over HTTPS
- **bash-completion** - Bash command completion
- **bat** - Cat clone with syntax highlighting and line numbers
- **build-essential** - GCC, make, g++ and other essential build tools
- **ca-certificates** - SSL/TLS root certificates for HTTPS
- **clang** - C/C++ compiler based on LLVM
- **cmake** - Cross-platform build system generator
- **curl** - Command-line HTTP client for downloading files
- **dnsutils** - DNS lookup tools (dig, nslookup, host)
- **fd-find** - Fast alternative to find with better defaults
- **file** - Determine file type by content
- **fzf** - Fuzzy finder for interactive filtering
- **gdb** - GNU debugger for debugging programs
- **git** - Distributed version control system
- **git-lfs** - Git extension for handling large files
- **gnupg** - GNU Privacy Guard for encryption and signing
- **htop** - Interactive process viewer with colors
- **iproute2** - Modern network tools (ip, ss, bridge)
- **iputils-ping** - ICMP ping utility
- **jq** - Command-line JSON processor
- **less** - Terminal pager for viewing long output
- **libbz2-dev** - bzip2 compression library development files
- **libclang-dev** - Clang development headers
- **libffi-dev** - Foreign function interface development files
- **libgdbm-dev** - GDBM database library development files
- **liblzma-dev** - LZMA compression development files
- **libncurses5-dev** - ncurses terminal UI library (legacy version)
- **libncursesw5-dev** - ncurses wide character support
- **libreadline-dev** - GNU readline library development files
- **libsqlite3-dev** - SQLite3 database development files
- **libssl-dev** - OpenSSL development files
- **libxml2-dev** - XML library development files
- **libxmlsec1-dev** - XML security library development files
- **libyaml-dev** - YAML parsing library development files
- **lld** - LLVM linker
- **locales** - Locale data for internationalization
- **llvm** - LLVM compiler toolchain base
- **man-db** - Manual page database and viewer
- **manpages** - Manual page files
- **nano** - Simple text editor
- **neovim** - Modern fork of vim
- **net-tools** - Legacy network utilities (ifconfig, netstat, route)
- **ninja-build** - Small build system focused on speed
- **openssh-client** - SSH client (ssh, scp, sftp)
- **openssh-server** - SSH server daemon
- **pkg-config** - Library configuration tool
- **procps** - Process utilities (ps, top, uptime)
- **psmisc** - Miscellaneous process utilities (pkill, pstree)
- **python-is-python3** - Symlinks python to python3
- **python3** - Python 3 interpreter
- **python3-dev** - Python 3 development headers
- **python3-pip** - Python package manager
- **python3-setuptools** - Python packaging tools
- **python3-venv** - Python 3 virtual environment
- **python3-wheel** - Python wheel package format
- **ripgrep** - Fast grep replacement optimized for code
- **rsync** - Fast file transfer and sync utility
- **shellcheck** - Shell script static analysis tool
- **sqlite3** - SQLite command-line interface
- **strace** - System call tracer for debugging
- **sudo** - Execute commands as another user (passwordless for dev)
- **tmux** - Terminal multiplexer
- **tree** - Display directory tree structure
- **unzip** - ZIP archive extraction utility
- **vim** - Text editor
- **wget** - Non-interactive file downloader
- **xz-utils** - XZ compression utilities
- **zip** - ZIP archive creation utility
- **zsh** - Z shell with extended features

Rust toolchain:
- `rustup`
- `cargo`
- `rustc`
- `clippy`
- `rustfmt`
- `rust-analyzer`

Python toolchain:
- `python3`
- `python3-dev`
- `pip`
- `pipx`
- `venv`
- `uv`
- `pytest`
- `mypy`
- `black`
- `ruff`
- `ipython`

Node / agent tooling:
- Node.js from NodeSource `20.x` by default
- `npm`
- `pi`
- guest `agentfs`

`pi` is wrapped to extract a prebuilt package archive into `/tmp/pi-package-<uid>` on first launch, which avoids walking the NFS-backed package tree at runtime.

Relevant development libraries installed in the guest:
- `libssl-dev`
- `libsqlite3-dev`
- `libffi-dev`
- `libclang-dev`
- `libxml2-dev`
- `libxmlsec1-dev`
- `libyaml-dev`
- `libbz2-dev`
- `liblzma-dev`
- `libreadline-dev`
- `libgdbm-dev`
- `libncurses5-dev`
- `libncursesw5-dev`

## Build

Single command for first-time setup and launch:

```bash
cd /home/dev/Workspace/firecracker-agentfs
chmod +x alcatraz.core/run.sh alcatraz.core/build-kernel.sh alcatraz.core/build-rootfs.sh alcatraz.core/firecracker.sh
sudo -E ./alcatraz.core/run.sh
```

`run.sh`:
- builds the kernel if it is missing
- builds the rootfs if it is missing
- starts Firecracker with AgentFS

After kernel config changes, force a kernel rebuild:

```bash
sudo -E ./alcatraz.core/run.sh --build-kernel
```

After rootfs changes like SSH setup, force a rootfs rebuild too:

```bash
RESET_AGENTFS=1 sudo -E ./alcatraz.core/run.sh --build-all my-agent
```

If you want the explicit manual steps instead:

```bash
cd /home/dev/Workspace/firecracker-agentfs
chmod +x alcatraz.core/build-kernel.sh alcatraz.core/build-rootfs.sh alcatraz.core/firecracker.sh
sudo -E ./alcatraz.core/build-kernel.sh
sudo -E ./alcatraz.core/build-rootfs.sh
```

What those scripts do:
- `build-kernel.sh` installs host build deps with `apt`, clones the Amazon Linux kernel, enables Firecracker/NFS/dev-friendly kernel options, and builds `vmlinux`
- if `linux-amazon/` already exists and is clean, `build-kernel.sh` will switch it to the requested `KERNEL_TAG` automatically
- `build-rootfs.sh` installs host-side bootstrap deps, recreates `alcatraz.core/rootfs`, bootstraps Ubuntu `noble`, installs guest packages (including `tini` for PID 1 supervision), Rust, Python tools, Pi, and guest AgentFS, then writes `/init` (`exec tini -- sshd -D`) and guest profile files

## Run

```bash
sudo -E ./alcatraz.core/run.sh
```

or directly:

```bash
./alcatraz.core/firecracker.sh
```

Use a custom AgentFS overlay id:

```bash
sudo -E ./alcatraz.core/run.sh my-agent
```

or:

```bash
./alcatraz.core/firecracker.sh my-agent
```

If the base rootfs changed and you want to recreate the overlay for an existing id:

```bash
RESET_AGENTFS=1 sudo -E ./alcatraz.core/run.sh my-agent
```

or:

```bash
RESET_AGENTFS=1 ./alcatraz.core/firecracker.sh my-agent
```

Stop the VM with `Ctrl+C`.
```

## Reaching Host Localhost Services

Guest `127.0.0.1` is the VM itself, not the host. To reach host services that are bound only to host localhost, connect from the VM to the host TAP IP instead:

```text
172.16.0.1:8000-8010
```

By default, `firecracker.sh` forwards guest TCP connections to `172.16.0.1:8000-8010` into host `127.0.0.1:8000-8010`.

Examples from inside the VM:

```bash
curl http://172.16.0.1:8000
curl http://172.16.0.1:8010
```

To change or disable that forwarding:

```bash
HOST_LOOPBACK_PORTS=3000,5173 sudo -E ./alcatraz.core/run.sh
HOST_LOOPBACK_PORTS= sudo -E ./alcatraz.core/run.sh
```

## Persistence Model

- Base image: `alcatraz.core/rootfs`
- Overlay DB: `alcatraz.core/.agentfs/<agent-id>.db`
- Base stamp: `alcatraz.core/.agentfs/<agent-id>.base-stamp`

The launcher hashes `alcatraz.core/rootfs/etc/alcatraz-release` and refuses to silently reuse an overlay against a changed base rootfs. If the base image changed, either:
- use a new agent id, or
- run with `RESET_AGENTFS=1`

## Runtime Notes

- `vm_config.json` is not checked in anymore; it is generated as a temporary file at launch time.
- If the host `firecracker` binary is missing or on the wrong version, `firecracker.sh` downloads `v1.15.1` into `alcatraz.core/bin/`.
- The host `agentfs` binary is not auto-installed by the launcher. Install it yourself first.
- The launcher warns if the host `agentfs` version does not match the expected target version.
- The guest rootfs is served from AgentFS over NFSv3.
- `firecracker.sh` kills stale AgentFS NFS exports on the configured bind/port before starting a new one.
- Guest boot logs are visible by default. To suppress them, use `GUEST_KERNEL_QUIET=1`.
- The guest shell is started under `setsid --ctty` so it can claim the Firecracker serial console as its controlling TTY.

## Useful Overrides

Build-time:

```bash
sudo -E ./alcatraz.core/run.sh --build-all
sudo -E ./alcatraz.core/run.sh --build-rootfs
RESET_AGENTFS=1 sudo -E ./alcatraz.core/run.sh --build-rootfs my-agent
NODE_MAJOR=20 ./alcatraz.core/build-rootfs.sh
RUST_TOOLCHAIN=stable ./alcatraz.core/build-rootfs.sh
VM_USER=dev VM_USER_PASSWORD=dev VM_HOSTNAME=alcatraz ./alcatraz.core/build-rootfs.sh
KERNEL_TAG=microvm-kernel-6.1.167-27.319.amzn2023 ./alcatraz.core/build-kernel.sh
```

Run-time:

```bash
VM_VCPUS=4 VM_MEM_MIB=4096 ./alcatraz.core/firecracker.sh
HOST_IFACE=wlp194s0 ./alcatraz.core/firecracker.sh
TAP_DEV=fc-tap1 HOST_TAP_IP=172.16.1.1 VM_IP=172.16.1.2 VM_SUBNET=172.16.1.0/24 ./alcatraz.core/firecracker.sh
NFS_PORT=11112 ./alcatraz.core/firecracker.sh
GUEST_KERNEL_QUIET=1 ./alcatraz.core/firecracker.sh
```

Available environment knobs used by the scripts:
- `build-rootfs.sh`
  - `ROOTFS_DIR`
  - `ROOTFS_RELEASE`
  - `ROOTFS_MIRROR`
  - `ROOTFS_ARCH`
  - `VM_USER`
  - `VM_USER_PASSWORD`
  - `VM_HOSTNAME`
  - `NODE_MAJOR`
  - `PI_PACKAGE`
  - `RUST_TOOLCHAIN`
  - `EXTRA_APT_PACKAGES`
  - `EXTRA_NPM_PACKAGES`
  - `HOST_SSH_DIR`
- `run.sh`
  - `BUILD_KERNEL_MODE`
  - `BUILD_ROOTFS_MODE`
- `build-kernel.sh`
  - `KERNEL_DIR`
  - `KERNEL_TAG`
- `firecracker.sh`
  - `ROOTFS_DIR`
  - `KERNEL_PATH`
  - `LOCAL_BIN_DIR`
  - `FIRECRACKER_VERSION`
  - `AGENTFS_VERSION`
  - `FIRECRACKER_BIN`
  - `AGENTFS_BIN`
  - `TAP_DEV`
  - `HOST_TAP_IP`
  - `VM_IP`
  - `VM_SUBNET`
  - `HOST_IFACE`
  - `NFS_PORT`
  - `HOST_LOOPBACK_PORTS`
  - `VM_HOSTNAME`
  - `VM_VCPUS`
  - `VM_MEM_MIB`
  - `FIRECRACKER_LOG_LEVEL`
  - `RESET_AGENTFS`
  - `GUEST_KERNEL_LOGLEVEL`
  - `GUEST_KERNEL_QUIET`

## Inspecting Changes

```bash
agentfs diff <agent-id>
```

You can also inspect the AgentFS database directly from `alcatraz.core/.agentfs/` if needed.
