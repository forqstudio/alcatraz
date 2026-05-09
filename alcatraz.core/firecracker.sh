#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOTFS_DIR="${ROOTFS_DIR:-${SCRIPT_DIR}/rootfs}"
KERNEL_PATH="${KERNEL_PATH:-${SCRIPT_DIR}/linux-amazon/vmlinux}"
LOCAL_BIN_DIR="${LOCAL_BIN_DIR:-${SCRIPT_DIR}/bin}"

FIRECRACKER_VERSION="${FIRECRACKER_VERSION:-v1.15.1}"
AGENTFS_VERSION="${AGENTFS_VERSION:-0.6.4}"

FIRECRACKER_BIN="${FIRECRACKER_BIN:-}"
AGENTFS_BIN="${AGENTFS_BIN:-}"

AGENT_ID="${1:-firecracker-dev}"
AGENTFS_DIR="${SCRIPT_DIR}/.agentfs"
DB_PATH="${AGENTFS_DIR}/${AGENT_ID}.db"
BASE_STAMP_FILE="${ROOTFS_DIR}/etc/alcatraz-release"
BASE_STAMP_PATH="${AGENTFS_DIR}/${AGENT_ID}.base-stamp"

TAP_DEV="${TAP_DEV:-fc-tap0}"
HOST_TAP_IP="${HOST_TAP_IP:-172.16.0.1}"
VM_IP="${VM_IP:-172.16.0.2}"
VM_SUBNET="${VM_SUBNET:-172.16.0.0/24}"
HOST_IFACE="${HOST_IFACE:-$(ip route show default | awk '/default/ {print $5; exit}')}"
NFS_PORT="${NFS_PORT:-11111}"
HOST_LOOPBACK_PORTS="${HOST_LOOPBACK_PORTS:-8000:8010}"

VM_HOSTNAME="${VM_HOSTNAME:-alcatraz}"

# Dev-only: where to look for host pubkeys to plant into the guest's
# authorized_keys at boot. Defaults to the invoking user's ~/.ssh — we explicitly
# resolve SUDO_USER's home rather than $HOME so `sudo ./run.sh` (no -E) works
# the same as `sudo -E ./run.sh`. Set HOST_SSH_DIR= (empty) to skip planting
# and force cert-only access for this VM.
if [ -z "${HOST_SSH_DIR+x}" ]; then
    if [ -n "${SUDO_USER:-}" ]; then
        HOST_SSH_DIR="$(getent passwd "${SUDO_USER}" | cut -d: -f6)/.ssh"
    else
        HOST_SSH_DIR="${HOME}/.ssh"
    fi
fi
VM_VCPUS="${VM_VCPUS:-4}"
VM_MEM_MIB="${VM_MEM_MIB:-8192}"
FIRECRACKER_LOG_LEVEL="${FIRECRACKER_LOG_LEVEL:-Error}"
RESET_AGENTFS="${RESET_AGENTFS:-0}"
GUEST_KERNEL_LOGLEVEL="${GUEST_KERNEL_LOGLEVEL:-7}"
GUEST_KERNEL_QUIET="${GUEST_KERNEL_QUIET:-0}"

AGENTFS_SERVER_PID=""
VM_CONFIG=""
PREV_IP_FORWARD=""
NAT_COMMENT="firecracker-agentfs"

log() {
    echo "==> $*"
}

warn() {
    echo "Warning: $*" >&2
}

die() {
    echo "Error: $*" >&2
    exit 1
}

resolve_firecracker_bin() {
    if [ -n "${FIRECRACKER_BIN}" ]; then
        [ -x "${FIRECRACKER_BIN}" ] || die "FIRECRACKER_BIN is set but not executable: ${FIRECRACKER_BIN}"
        return
    fi

    if command -v firecracker >/dev/null 2>&1; then
        local system_firecracker
        local system_version
        system_firecracker="$(command -v firecracker)"
        system_version="$("${system_firecracker}" --version 2>/dev/null | awk 'NR==1 {print $2}')"
        if [ "${system_version}" = "${FIRECRACKER_VERSION}" ]; then
            FIRECRACKER_BIN="${system_firecracker}"
            return
        fi
        warn "Host firecracker is ${system_version:-unknown}; using ${FIRECRACKER_VERSION} for this VM."
    fi

    FIRECRACKER_BIN="${LOCAL_BIN_DIR}/firecracker-${FIRECRACKER_VERSION}"
    if [ -x "${FIRECRACKER_BIN}" ]; then
        return
    fi

    log "Downloading Firecracker ${FIRECRACKER_VERSION}"
    mkdir -p "${LOCAL_BIN_DIR}"
    (
        cd "${LOCAL_BIN_DIR}"
        curl -fsSL -o "firecracker-${FIRECRACKER_VERSION}.tgz" \
            "https://github.com/firecracker-microvm/firecracker/releases/download/${FIRECRACKER_VERSION}/firecracker-${FIRECRACKER_VERSION}-x86_64.tgz"
        tar xf "firecracker-${FIRECRACKER_VERSION}.tgz"
        mv "release-${FIRECRACKER_VERSION}-x86_64/firecracker-${FIRECRACKER_VERSION}-x86_64" "${FIRECRACKER_BIN}"
        chmod +x "${FIRECRACKER_BIN}"
        rm -rf "release-${FIRECRACKER_VERSION}-x86_64" "firecracker-${FIRECRACKER_VERSION}.tgz"
    )
}

resolve_agentfs_bin() {
    if [ -n "${AGENTFS_BIN}" ]; then
        [ -x "${AGENTFS_BIN}" ] || die "AGENTFS_BIN is set but not executable: ${AGENTFS_BIN}"
        return
    fi

    if ! command -v agentfs >/dev/null 2>&1; then
        die "agentfs is not installed on the host. Install it with: curl -fsSL https://agentfs.ai/install | bash"
    fi

    AGENTFS_BIN="$(command -v agentfs)"
    local host_version
    host_version="$("${AGENTFS_BIN}" --version 2>/dev/null | awk '{print $2}' | sed 's/^v//')"
    if [ "${host_version}" != "${AGENTFS_VERSION}" ]; then
        warn "Host agentfs is ${host_version:-unknown}; expected ${AGENTFS_VERSION}. Upgrade recommended for the latest NFS implementation."
    fi
}

ensure_requirements() {
    [ -f "${KERNEL_PATH}" ] || die "Kernel not found at ${KERNEL_PATH}. Run ./build-kernel.sh first."
    [ -d "${ROOTFS_DIR}" ] || die "Rootfs not found at ${ROOTFS_DIR}. Run ./build-rootfs.sh first."
    [ -f "${BASE_STAMP_FILE}" ] || die "Rootfs at ${ROOTFS_DIR} is missing etc/alcatraz-release. Rebuild it with ./build-rootfs.sh."
    [ -n "${HOST_IFACE}" ] || die "Could not determine the host uplink interface. Set HOST_IFACE explicitly."
}

compute_base_stamp() {
    if [ -f "${BASE_STAMP_FILE}" ]; then
        sha256sum "${BASE_STAMP_FILE}" | awk '{print $1}'
    else
        return 0
    fi
}

prepare_agentfs_overlay() {
    mkdir -p "${AGENTFS_DIR}"

    if [ "${RESET_AGENTFS}" = "1" ]; then
        log "Resetting AgentFS overlay for ${AGENT_ID}"
        rm -f "${DB_PATH}" "${DB_PATH}-wal" "${DB_PATH}-shm" "${BASE_STAMP_PATH}"
    fi

    local current_stamp
    current_stamp="$(compute_base_stamp || true)"

    if [ -f "${DB_PATH}" ] && [ -n "${current_stamp}" ] && [ -f "${BASE_STAMP_PATH}" ]; then
        if [ "$(cat "${BASE_STAMP_PATH}")" != "${current_stamp}" ]; then
            die "The rootfs base changed for ${AGENT_ID}. Re-run with RESET_AGENTFS=1 or choose a new agent id."
        fi
    fi

    if [ ! -f "${DB_PATH}" ]; then
        log "Initializing AgentFS overlay ${AGENT_ID}"
        (
            cd "${SCRIPT_DIR}"
            "${AGENTFS_BIN}" init --force --base "./rootfs" "${AGENT_ID}" >/dev/null
        )
    fi

    if [ -n "${current_stamp}" ]; then
        printf '%s\n' "${current_stamp}" > "${BASE_STAMP_PATH}"
    fi
}

plant_dev_authorized_keys() {
    # Inject the developer's host pubkeys into THIS VM's overlay only. The
    # base rootfs intentionally has no authorized_keys (so customer VMs
    # spawned by alcatraz.worker stay cert-only); this is a dev-convenience
    # path for the bare run.sh / firecracker.sh single-VM flow.
    if [ -z "${HOST_SSH_DIR}" ]; then
        log "HOST_SSH_DIR is empty — skipping dev authorized_keys plant (cert-only)"
        return
    fi
    if ! compgen -G "${HOST_SSH_DIR}/*.pub" >/dev/null 2>&1; then
        warn "No *.pub files in ${HOST_SSH_DIR}; skipping authorized_keys plant. Pass HOST_SSH_DIR=/path or run via 'sudo -E'."
        return
    fi

    local keys
    keys="$(cat "${HOST_SSH_DIR}"/*.pub)"
    log "Planting authorized_keys for dev VM ${AGENT_ID} from ${HOST_SSH_DIR}"
    (
        cd "${SCRIPT_DIR}"
        "${AGENTFS_BIN}" fs "${AGENT_ID}" write /home/al/.ssh/authorized_keys "${keys}" >/dev/null
    )
}

setup_tap() {
    sudo ip link del "${TAP_DEV}" 2>/dev/null || true
    sudo ip tuntap add dev "${TAP_DEV}" mode tap
    sudo ip addr add "${HOST_TAP_IP}/24" dev "${TAP_DEV}"
    sudo ip link set "${TAP_DEV}" up
}

iptables_ensure() {
    local table="$1"
    shift
    if [ -n "${table}" ]; then
        if ! sudo iptables -t "${table}" -C "$@" 2>/dev/null; then
            sudo iptables -t "${table}" -A "$@"
        fi
        return
    fi

    if ! sudo iptables -C "$@" 2>/dev/null; then
        sudo iptables -A "$@"
    fi
}

iptables_delete() {
    local table="$1"
    shift
    if [ -n "${table}" ]; then
        sudo iptables -t "${table}" -D "$@" 2>/dev/null || true
        return
    fi

    sudo iptables -D "$@" 2>/dev/null || true
}

setup_nat() {
    PREV_IP_FORWARD="$(sysctl -n net.ipv4.ip_forward 2>/dev/null || echo 0)"
    sudo sysctl -w net.ipv4.ip_forward=1 >/dev/null
    sudo sysctl -w "net.ipv4.conf.${TAP_DEV}.route_localnet=1" >/dev/null

    iptables_ensure nat POSTROUTING -s "${VM_SUBNET}" -o "${HOST_IFACE}" -m comment --comment "${NAT_COMMENT}" -j MASQUERADE
    iptables_ensure "" FORWARD -i "${HOST_IFACE}" -o "${TAP_DEV}" -m state --state RELATED,ESTABLISHED -m comment --comment "${NAT_COMMENT}" -j ACCEPT
    iptables_ensure "" FORWARD -i "${TAP_DEV}" -o "${HOST_IFACE}" -m comment --comment "${NAT_COMMENT}" -j ACCEPT

    if [ -n "${HOST_LOOPBACK_PORTS}" ]; then
        iptables_ensure nat PREROUTING -i "${TAP_DEV}" -d "${HOST_TAP_IP}" -p tcp -m multiport --dports "${HOST_LOOPBACK_PORTS}" -m comment --comment "${NAT_COMMENT}-loopback" -j DNAT --to-destination 127.0.0.1
        iptables_ensure "" INPUT -i "${TAP_DEV}" -p tcp -d 127.0.0.1 -m multiport --dports "${HOST_LOOPBACK_PORTS}" -m comment --comment "${NAT_COMMENT}-loopback" -j ACCEPT
    fi
}

stop_stale_agentfs_nfs() {
    pkill -f "${AGENTFS_BIN} serve nfs --bind ${HOST_TAP_IP} --port ${NFS_PORT}" 2>/dev/null || true
    sleep 0.5
}

start_agentfs_nfs() {
    log "Starting AgentFS NFS export on ${HOST_TAP_IP}:${NFS_PORT}"
    (
        cd "${SCRIPT_DIR}"
        "${AGENTFS_BIN}" serve nfs --bind "${HOST_TAP_IP}" --port "${NFS_PORT}" "${AGENT_ID}" >/dev/null 2>&1
    ) &
    AGENTFS_SERVER_PID="$!"
    sleep 1

    if ! kill -0 "${AGENTFS_SERVER_PID}" 2>/dev/null; then
        die "AgentFS NFS server failed to start on port ${NFS_PORT}"
    fi
}

create_vm_config() {
    local boot_verbosity_args
    if [ "${GUEST_KERNEL_QUIET}" = "1" ]; then
        boot_verbosity_args="quiet loglevel=0"
    else
        boot_verbosity_args="loglevel=${GUEST_KERNEL_LOGLEVEL} printk.devkmsg=on"
    fi

    VM_CONFIG="$(mktemp "${SCRIPT_DIR}/vm_config.XXXXXX.json")"
    cat > "${VM_CONFIG}" <<EOF
{
  "boot-source": {
    "kernel_image_path": "${KERNEL_PATH}",
    "boot_args": "console=ttyS0 reboot=k panic=1 pci=off ${boot_verbosity_args} ip=${VM_IP}::${HOST_TAP_IP}:255.255.255.0:${VM_HOSTNAME}:eth0:off root=/dev/nfs nfsroot=${HOST_TAP_IP}:/,nfsvers=3,tcp,nolock,port=${NFS_PORT},mountport=${NFS_PORT} rw init=/init"
  },
  "drives": [],
  "network-interfaces": [
    {
      "iface_id": "eth0",
      "guest_mac": "AA:FC:00:00:00:01",
      "host_dev_name": "${TAP_DEV}"
    }
  ],
  "machine-config": {
    "vcpu_count": ${VM_VCPUS},
    "mem_size_mib": ${VM_MEM_MIB}
  }
}
EOF
}

cleanup() {
    if [ -n "${AGENTFS_SERVER_PID}" ] && kill -0 "${AGENTFS_SERVER_PID}" 2>/dev/null; then
        kill "${AGENTFS_SERVER_PID}" 2>/dev/null || true
        wait "${AGENTFS_SERVER_PID}" 2>/dev/null || true
    fi

    sudo ip link del "${TAP_DEV}" 2>/dev/null || true

    iptables_delete nat POSTROUTING -s "${VM_SUBNET}" -o "${HOST_IFACE}" -m comment --comment "${NAT_COMMENT}" -j MASQUERADE
    iptables_delete "" FORWARD -i "${HOST_IFACE}" -o "${TAP_DEV}" -m state --state RELATED,ESTABLISHED -m comment --comment "${NAT_COMMENT}" -j ACCEPT
    iptables_delete "" FORWARD -i "${TAP_DEV}" -o "${HOST_IFACE}" -m comment --comment "${NAT_COMMENT}" -j ACCEPT

    if [ -n "${HOST_LOOPBACK_PORTS}" ]; then
        iptables_delete nat PREROUTING -i "${TAP_DEV}" -d "${HOST_TAP_IP}" -p tcp -m multiport --dports "${HOST_LOOPBACK_PORTS}" -m comment --comment "${NAT_COMMENT}-loopback" -j DNAT --to-destination 127.0.0.1
        iptables_delete "" INPUT -i "${TAP_DEV}" -p tcp -d 127.0.0.1 -m multiport --dports "${HOST_LOOPBACK_PORTS}" -m comment --comment "${NAT_COMMENT}-loopback" -j ACCEPT
    fi

    if [ "${PREV_IP_FORWARD:-}" = "0" ]; then
        sudo sysctl -w net.ipv4.ip_forward=0 >/dev/null || true
    fi

    [ -n "${VM_CONFIG}" ] && rm -f "${VM_CONFIG}"

    echo
    echo "Changes saved. View them with: ${AGENTFS_BIN:-agentfs} diff ${AGENT_ID}"
}
trap cleanup EXIT

resolve_firecracker_bin
resolve_agentfs_bin
ensure_requirements
prepare_agentfs_overlay
plant_dev_authorized_keys
setup_tap
setup_nat
stop_stale_agentfs_nfs
start_agentfs_nfs
create_vm_config

log "Launching Firecracker (${VM_VCPUS} vCPU, ${VM_MEM_MIB} MiB)"
"${FIRECRACKER_BIN}" --no-api --config-file "${VM_CONFIG}" --level "${FIRECRACKER_LOG_LEVEL}"
