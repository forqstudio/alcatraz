package vm

import (
	"github.com/google/uuid"
)

const (
	DefaultMaxVMs = 5

	FirecrackerBin = "../alcatraz.core/bin/firecracker-v1.15.1"
	KernelPath     = "../alcatraz.core/linux-amazon/vmlinux"
	RootfsPath     = "../alcatraz.core/rootfs"
	AgentfsData    = "../alcatraz.core/.agentfs"

	CNIConfDir  = "/etc/cni/net.d"
	CNIBinDir   = "/opt/cni/bin"
	BridgeName  = "alcatraz-bridge"
	SubnetCIDR  = "172.16.0.0/24"
	GatewayIP   = "172.16.0.1"
	NFSBasePort = 8000

	VMHostname = "alcatraz"
	GuestMAC   = "AA:FC:00:00:00:01"

	DefaultVCPUs      = 4
	DefaultMemMib     = 8192
	DefaultKernelArgs = "loglevel=7 printk.devkmsg=on"
)

type VirtualMachineConfig struct {
	MaxVMs         int
	CNIConfDir     string
	AgentfsData    string
	FirecrackerBin string
	Rootfs         string
	Kernel         string
}

func DefaultConfig() *VirtualMachineConfig {
	return &VirtualMachineConfig{
		MaxVMs:         DefaultMaxVMs,
		FirecrackerBin: FirecrackerBin,
		Rootfs:         RootfsPath,
		Kernel:         KernelPath,
		AgentfsData:    AgentfsData,
		CNIConfDir:     CNIConfDir,
	}
}

type CreateVirtualMachineInput struct {
	ID         string `json:"id,omitempty"`
	VCPUs      int    `json:"vcpus,omitempty"`
	MemoryMib  int    `json:"memory_mib,omitempty"`
	KernelArgs string `json:"kernel_args,omitempty"`
}

// applyDefaults fills any zero-valued fields with their package defaults.
// Mutates the receiver.
func (input *CreateVirtualMachineInput) applyDefaults() {
	if input.ID == "" {
		input.ID = uuid.New().String()
	}
	if input.VCPUs <= 0 {
		input.VCPUs = DefaultVCPUs
	}
	if input.MemoryMib <= 0 {
		input.MemoryMib = DefaultMemMib
	}
	if input.KernelArgs == "" {
		input.KernelArgs = DefaultKernelArgs
	}
}
