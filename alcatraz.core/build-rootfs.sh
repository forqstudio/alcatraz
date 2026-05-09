#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOTFS_DIR="${ROOTFS_DIR:-${SCRIPT_DIR}/rootfs}"
ROOTFS_RELEASE="${ROOTFS_RELEASE:-noble}"
ROOTFS_MIRROR="${ROOTFS_MIRROR:-http://archive.ubuntu.com/ubuntu}"
ROOTFS_ARCH="${ROOTFS_ARCH:-amd64}"
VM_USER="${VM_USER:-al}"
VM_USER_PASSWORD="${VM_USER_PASSWORD:-dev}"
VM_HOSTNAME="${VM_HOSTNAME:-alcatraz}"
NODE_MAJOR="${NODE_MAJOR:-20}"
PI_PACKAGE="${PI_PACKAGE:-@mariozechner/pi-coding-agent@latest}"
RUST_TOOLCHAIN="${RUST_TOOLCHAIN:-stable}"
EXTRA_APT_PACKAGES="${EXTRA_APT_PACKAGES:-}"
EXTRA_NPM_PACKAGES="${EXTRA_NPM_PACKAGES:-}"
# Note: the rootfs is intentionally cert-only at build time. No host SSH
# pubkeys are baked in — that would put the developer's key in every customer
# VM and bypass the per-sandbox cert pipeline. The dev-only `run.sh` /
# `firecracker.sh` path injects authorized_keys into the per-VM AgentFS
# overlay at boot, scoped to that one VM.

BASE_APT_PACKAGES=(
    apt-utils
    apt-transport-https
    bash-completion
    bat
    build-essential
    ca-certificates
    clang
    cmake
    curl
    dnsutils
    fd-find
    file
    fzf
    gdb
    git
    git-lfs
    gnupg
    htop
    iproute2
    iputils-ping
    jq
    less
    libbz2-dev
    libclang-dev
    libffi-dev
    libgdbm-dev
    liblzma-dev
    libncurses5-dev
    libncursesw5-dev
    libreadline-dev
    libsqlite3-dev
    libssl-dev
    libxml2-dev
    libxmlsec1-dev
    libyaml-dev
    lld
    locales
    llvm
    man-db
    manpages
    nano
    neovim
    net-tools
    ninja-build
    openssh-client
    openssh-server
    pipx
    pkg-config
    procps
    psmisc
    python-is-python3
    python3
    python3-dev
    python3-pip
    python3-setuptools
    python3-venv
    python3-wheel
    ripgrep
    rsync
    tini
    shellcheck
    sqlite3
    strace
    sudo
    tmux
    tree
    unzip
    vim
    wget
    xz-utils
    zip
    zsh
)

log() {
    echo "==> $*"
}

require_host_packages() {
    log "Installing host-side build dependencies"
    sudo apt-get update -qq
    sudo apt-get install -y -qq ca-certificates curl debootstrap gpg rsync xz-utils >/dev/null
}

mount_chroot() {
    sudo mkdir -p "${ROOTFS_DIR}/dev/pts" "${ROOTFS_DIR}/proc" "${ROOTFS_DIR}/sys" "${ROOTFS_DIR}/run"
    mountpoint -q "${ROOTFS_DIR}/dev" || sudo mount --bind /dev "${ROOTFS_DIR}/dev"
    mountpoint -q "${ROOTFS_DIR}/dev/pts" || sudo mount --bind /dev/pts "${ROOTFS_DIR}/dev/pts"
    mountpoint -q "${ROOTFS_DIR}/proc" || sudo mount -t proc proc "${ROOTFS_DIR}/proc"
    mountpoint -q "${ROOTFS_DIR}/sys" || sudo mount -t sysfs sysfs "${ROOTFS_DIR}/sys"
    mountpoint -q "${ROOTFS_DIR}/run" || sudo mount --bind /run "${ROOTFS_DIR}/run"
    sudo cp /etc/resolv.conf "${ROOTFS_DIR}/etc/resolv.conf"
}

umount_chroot() {
    sudo umount -lf "${ROOTFS_DIR}/run" 2>/dev/null || true
    sudo umount -lf "${ROOTFS_DIR}/sys" 2>/dev/null || true
    sudo umount -lf "${ROOTFS_DIR}/proc" 2>/dev/null || true
    sudo umount -lf "${ROOTFS_DIR}/dev/pts" 2>/dev/null || true
    sudo umount -lf "${ROOTFS_DIR}/dev" 2>/dev/null || true
}

chroot_run() {
    sudo chroot "${ROOTFS_DIR}" /usr/bin/env DEBIAN_FRONTEND=noninteractive bash -lc "$1"
}

cleanup() {
    umount_chroot
}
trap cleanup EXIT

join_by() {
    local delimiter="$1"
    shift
    local first=1
    for value in "$@"; do
        if [ "${first}" -eq 1 ]; then
            printf '%s' "${value}"
            first=0
        else
            printf '%s%s' "${delimiter}" "${value}"
        fi
    done
}

log "Rebuilding rootfs at ${ROOTFS_DIR}"
require_host_packages
umount_chroot
sudo rm -rf "${ROOTFS_DIR}"
sudo mkdir -p "${ROOTFS_DIR}"

log "Bootstrapping Ubuntu ${ROOTFS_RELEASE}"
sudo debootstrap \
    --arch="${ROOTFS_ARCH}" \
    --variant=minbase \
    --components=main,universe \
    "${ROOTFS_RELEASE}" \
    "${ROOTFS_DIR}" \
    "${ROOTFS_MIRROR}" >/dev/null

mount_chroot

log "Upgrading base packages"
chroot_run "apt-get update && apt-get dist-upgrade -y"

log "Installing core developer tooling"
APT_PACKAGE_STRING="$(join_by ' ' "${BASE_APT_PACKAGES[@]}")"
if [ -n "${EXTRA_APT_PACKAGES}" ]; then
    APT_PACKAGE_STRING="${APT_PACKAGE_STRING} ${EXTRA_APT_PACKAGES}"
fi
chroot_run "apt-get install -y ${APT_PACKAGE_STRING}"

log "Adding the GitHub CLI apt repository"
chroot_run "install -d -m 0755 /etc/apt/keyrings"
chroot_run "curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg > /etc/apt/keyrings/githubcli-archive-keyring.gpg"
chroot_run "chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg"
chroot_run "echo 'deb [arch=${ROOTFS_ARCH} signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main' > /etc/apt/sources.list.d/github-cli.list"

log "Adding the NodeSource ${NODE_MAJOR}.x apt repository"
chroot_run "curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg"
chroot_run "chmod go+r /etc/apt/keyrings/nodesource.gpg"
chroot_run "echo 'deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_${NODE_MAJOR}.x nodistro main' > /etc/apt/sources.list.d/nodesource.list"

log "Installing fast-moving upstream tools"
chroot_run "apt-get update && apt-get install -y gh nodejs"
chroot_run "corepack enable || true"
chroot_run "npm install -g ${PI_PACKAGE}${EXTRA_NPM_PACKAGES:+ ${EXTRA_NPM_PACKAGES}}"
chroot_run "install -d -m 0755 /usr/local/share"
chroot_run "tar -C /usr/lib/node_modules -cf /usr/local/share/pi-coding-agent.tar '@mariozechner/pi-coding-agent'"
sudo tee "${ROOTFS_DIR}/usr/local/bin/pi" >/dev/null <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

PI_ARCHIVE_PATH="/usr/local/share/pi-coding-agent.tar"
PI_RUNTIME_DIR="/tmp/pi-package-${UID}"

if [ ! -f "${PI_RUNTIME_DIR}/dist/cli.js" ]; then
    rm -rf "${PI_RUNTIME_DIR}"
    mkdir -p "${PI_RUNTIME_DIR}"
    tar -xf "${PI_ARCHIVE_PATH}" -C "${PI_RUNTIME_DIR}" --strip-components=2 "@mariozechner/pi-coding-agent"
fi

export PI_PACKAGE_DIR="${PI_RUNTIME_DIR}"
exec /usr/bin/node "${PI_RUNTIME_DIR}/dist/cli.js" "$@"
EOF
sudo chmod 0755 "${ROOTFS_DIR}/usr/local/bin/pi"

log "Installing Rust toolchain and core Rust developer tools"
chroot_run "mkdir -p /usr/local/cargo /usr/local/rustup"
chroot_run "curl -fsSL https://sh.rustup.rs | env CARGO_HOME=/usr/local/cargo RUSTUP_HOME=/usr/local/rustup sh -s -- -y --no-modify-path --profile default --default-toolchain ${RUST_TOOLCHAIN}"
chroot_run "env CARGO_HOME=/usr/local/cargo RUSTUP_HOME=/usr/local/rustup /usr/local/cargo/bin/rustup component add rustfmt clippy rust-analyzer"

log "Installing AgentFS inside the guest"
chroot_run "mkdir -p /usr/local/cargo"
chroot_run "curl -fsSL https://agentfs.ai/install | env CARGO_HOME=/usr/local/cargo AGENTFS_NO_MODIFY_PATH=1 sh"
chroot_run "ln -sf /usr/local/cargo/bin/agentfs /usr/local/bin/agentfs"

log "Installing Python developer tooling"
chroot_run "mkdir -p /opt/pipx"
chroot_run "PIPX_HOME=/opt/pipx PIPX_BIN_DIR=/usr/local/bin pipx install --force uv"
chroot_run "PIPX_HOME=/opt/pipx PIPX_BIN_DIR=/usr/local/bin pipx install --force ruff"
chroot_run "PIPX_HOME=/opt/pipx PIPX_BIN_DIR=/usr/local/bin pipx install --force mypy"
chroot_run "PIPX_HOME=/opt/pipx PIPX_BIN_DIR=/usr/local/bin pipx install --force black"
chroot_run "PIPX_HOME=/opt/pipx PIPX_BIN_DIR=/usr/local/bin pipx install --force pytest"
chroot_run "PIPX_HOME=/opt/pipx PIPX_BIN_DIR=/usr/local/bin pipx install --force ipython"

log "Creating developer user and workspace"
chroot_run "id -u '${VM_USER}' >/dev/null 2>&1 || useradd -m -s /bin/bash -G sudo '${VM_USER}'"
chroot_run "echo '${VM_USER} ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/90-${VM_USER}"
chroot_run "chmod 0440 /etc/sudoers.d/90-${VM_USER}"
chroot_run "install -d -o '${VM_USER}' -g '${VM_USER}' /workspace"
chroot_run "ln -sf /usr/bin/fdfind /usr/local/bin/fd"
chroot_run "ln -sf /usr/bin/batcat /usr/local/bin/bat"
printf '%s:%s\n' "${VM_USER}" "${VM_USER_PASSWORD}" | sudo chroot "${ROOTFS_DIR}" chpasswd
VM_UID="$(awk -F: -v user="${VM_USER}" '$1 == user { print $3 }' "${ROOTFS_DIR}/etc/passwd")"
VM_GID="$(awk -F: -v user="${VM_USER}" '$1 == user { print $4 }' "${ROOTFS_DIR}/etc/passwd")"
[ -n "${VM_UID}" ] || { echo "Error: failed to resolve UID for ${VM_USER}" >&2; exit 1; }
[ -n "${VM_GID}" ] || { echo "Error: failed to resolve GID for ${VM_USER}" >&2; exit 1; }

log "Configuring SSH access"
chroot_run "install -d -m 0755 /etc/ssh/sshd_config.d"
# Cert auth is the customer-facing path:
#   - sshd validates user certs signed by alcatraz.api's CA (TrustedUserCAKeys)
#   - a cert principal must appear in AuthorizedPrincipalsFile to be accepted
# The worker writes both files into the AgentFS overlay at spawn:
#   /etc/ssh/trusted_user_ca_keys  ← API's CA pubkey (gates which signers we trust)
#   /etc/ssh/auth_principals/al    ← sandbox UUID (gates which cert is for THIS VM)
# Empty stubs are pre-created at build so sshd starts cleanly even on a bare
# rootfs boot; the worker's overlay write replaces them per-VM.
sudo tee "${ROOTFS_DIR}/etc/ssh/sshd_config.d/alcatraz.conf" >/dev/null <<EOF
PasswordAuthentication no
PubkeyAuthentication yes
KbdInteractiveAuthentication no
PermitRootLogin no
UsePAM no
X11Forwarding no
PrintMotd no
TrustedUserCAKeys /etc/ssh/trusted_user_ca_keys
AuthorizedPrincipalsFile /etc/ssh/auth_principals/%u
EOF
sudo install -d -m 0755 "${ROOTFS_DIR}/etc/ssh/auth_principals"
sudo touch "${ROOTFS_DIR}/etc/ssh/auth_principals/${VM_USER}"
sudo chmod 0644 "${ROOTFS_DIR}/etc/ssh/auth_principals/${VM_USER}"
sudo touch "${ROOTFS_DIR}/etc/ssh/trusted_user_ca_keys"
sudo chmod 0644 "${ROOTFS_DIR}/etc/ssh/trusted_user_ca_keys"
chroot_run "ssh-keygen -A"
# .ssh dir is created with correct perms here so the guest can write into it
# at boot (e.g. firecracker.sh plants authorized_keys into the per-VM overlay
# at this exact path). authorized_keys is intentionally NOT planted here —
# see the comment near the top of this file.
sudo install -d -m 0700 -o "${VM_UID}" -g "${VM_GID}" "${ROOTFS_DIR}/home/${VM_USER}/.ssh"

log "Writing guest configuration files"
sudo tee "${ROOTFS_DIR}/etc/hostname" >/dev/null <<EOF
${VM_HOSTNAME}
EOF

sudo tee "${ROOTFS_DIR}/etc/resolv.conf" >/dev/null <<'EOF'
nameserver 1.1.1.1
nameserver 8.8.8.8
EOF

sudo tee "${ROOTFS_DIR}/etc/profile.d/agent-dev.sh" >/dev/null <<'EOF'
export LANG=C.UTF-8
export LC_ALL=C.UTF-8
export PATH="/usr/local/cargo/bin:/usr/local/bin:${PATH}"
export CARGO_HOME="/usr/local/cargo"
export RUSTUP_HOME="/usr/local/rustup"
export PIPX_HOME="/opt/pipx"
export PIPX_BIN_DIR="/usr/local/bin"
export EDITOR="${EDITOR:-nvim}"
export VISUAL="${VISUAL:-$EDITOR}"
EOF

sudo tee "${ROOTFS_DIR}/home/${VM_USER}/.bash_profile" >/dev/null <<'EOF'
if [ -f ~/.bashrc ]; then
    . ~/.bashrc
fi

if [ -d /workspace ]; then
    cd /workspace
fi
EOF

sudo tee "${ROOTFS_DIR}/home/${VM_USER}/.bashrc" >/dev/null <<'EOF'
export PATH="/usr/local/cargo/bin:/usr/local/bin:${PATH}"
alias ll='ls -alF'
EOF

sudo tee "${ROOTFS_DIR}/workspace/README.txt" >/dev/null <<'EOF'
This is the writable workspace inside the Firecracker VM.

Clone or copy repositories here if you want them tracked by AgentFS.
EOF

sudo tee "${ROOTFS_DIR}/etc/motd" >/dev/null <<EOF
AgentFS Firecracker Developer VM

Preinstalled tools:
  - git / gh
  - node / npm / pi
  - rustup / cargo / rustc / clippy / rustfmt / rust-analyzer
  - python / pip / pipx / uv / pytest / mypy / black / ruff / ipython
  - agentfs
  - ripgrep / fd / bat / jq / tmux / neovim / clang / cmake / gdb / pkg-config

Workspace: /workspace
User: ${VM_USER} (passwordless sudo)
SSH: ssh ${VM_USER}@172.16.0.2
Password: ${VM_USER_PASSWORD}
EOF

sudo tee "${ROOTFS_DIR}/etc/alcatraz-release" >/dev/null <<EOF
base_os=ubuntu:${ROOTFS_RELEASE}
node_channel=${NODE_MAJOR}.x
pi_package=${PI_PACKAGE}
rust_toolchain=${RUST_TOOLCHAIN}
vm_user=${VM_USER}
ssh_password=${VM_USER_PASSWORD}
built_at=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
EOF

sudo tee "${ROOTFS_DIR}/init" >/dev/null <<EOF
#!/bin/bash
set -euo pipefail

mountpoint -q /proc || mount -t proc proc /proc
mountpoint -q /sys || mount -t sysfs sysfs /sys
mountpoint -q /dev || mount -t devtmpfs devtmpfs /dev
mkdir -p /dev/pts
mountpoint -q /dev/pts || mount -t devpts devpts /dev/pts
mkdir -p /run /tmp
mountpoint -q /run || mount -t tmpfs -o mode=0755,nosuid,nodev tmpfs /run
mountpoint -q /tmp || mount -t tmpfs -o mode=1777,nosuid,nodev tmpfs /tmp
hostname "${VM_HOSTNAME}"
ip link set lo up 2>/dev/null || true
ip link set eth0 up 2>/dev/null || true
mkdir -p /run/sshd
install -d -m 0700 /run/ssh-hostkeys
[ -f /run/ssh-hostkeys/ssh_host_rsa_key ] || ssh-keygen -q -N '' -t rsa -f /run/ssh-hostkeys/ssh_host_rsa_key
[ -f /run/ssh-hostkeys/ssh_host_ecdsa_key ] || ssh-keygen -q -N '' -t ecdsa -f /run/ssh-hostkeys/ssh_host_ecdsa_key
[ -f /run/ssh-hostkeys/ssh_host_ed25519_key ] || ssh-keygen -q -N '' -t ed25519 -f /run/ssh-hostkeys/ssh_host_ed25519_key

cat /etc/motd
echo

# tini is PID 1: forwards signals (SIGTERM from the host shuts the VM down
# cleanly) and reaps zombies (orphaned grandchildren of customer SSH sessions
# don't accumulate). sshd runs in the foreground (-D) so its lifetime is the
# VM's lifetime — if sshd ever exits, tini exits with its code, the kernel
# panics (panic=1 reboot=k), and the worker observes vm.exit and publishes
# vm.destroyed. Customer access is via SSH; the serial console is not
# user-facing, so PID 1 owns sshd directly rather than spawning a shell.
exec /usr/bin/tini -- /usr/sbin/sshd -D \
    -o HostKey=/run/ssh-hostkeys/ssh_host_rsa_key \
    -o HostKey=/run/ssh-hostkeys/ssh_host_ecdsa_key \
    -o HostKey=/run/ssh-hostkeys/ssh_host_ed25519_key
EOF

sudo chmod +x "${ROOTFS_DIR}/init"
chroot_run "chown '${VM_USER}:${VM_USER}' /home/${VM_USER}/.bash_profile /home/${VM_USER}/.bashrc /workspace/README.txt"
if [ -d "${ROOTFS_DIR}/home/${VM_USER}/.ssh" ]; then
    chroot_run "chown -R '${VM_USER}:${VM_USER}' /home/${VM_USER}/.ssh"
fi

log "Cleaning package caches"
chroot_run "apt-get clean && rm -rf /var/lib/apt/lists/* /root/.npm /tmp/* /var/tmp/*"

log "Done. Rootfs created at ${ROOTFS_DIR}"
