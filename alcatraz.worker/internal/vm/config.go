package vm

import (
	"os"
	"strconv"

	"github.com/google/uuid"
	"github.com/joho/godotenv"
)

const EnvFile = ".env"

const (
	DefaultMaxVMs = 5

	FirecrackerBin = "../alcatraz.core/bin/firecracker-v1.15.1"
	KernelPath     = "../alcatraz.core/linux-amazon/vmlinux"
	RootfsPath     = "../alcatraz.core/rootfs"
	AgentfsData    = "../alcatraz.core/.agentfs"
	AgentfsBin     = "/home/dev/.cargo/bin/agentfs"

	BaseTapDev    = "fc-tap"
	BaseHostTapIP = "172.16.0.1"
	BaseVMIP      = "172.16.0.2"
	BaseNFSPort   = 11111

	VMHostname = "alcatraz"
	GuestMAC   = "AA:FC:00:00:00:01"

	DefaultVCPUs      = 4
	DefaultMemMib     = 8192
	DefaultKernelArgs = "loglevel=7 printk.devkmsg=on"
)

type VirtualMachineConfig struct {
	MaxVMs         int
	AgentfsBin     string
	AgentfsData    string
	FirecrackerBin string
	Rootfs         string
	Kernel         string
}

func LoadConfig() (*VirtualMachineConfig, error) {
	if err := godotenv.Load(EnvFile); err != nil {
		return nil, err
	}

	cfg := &VirtualMachineConfig{}

	if v := os.Getenv("MAX_VMS"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			cfg.MaxVMs = n
		}
	}
	if v := os.Getenv("FIRECRACKER_BIN"); v != "" {
		cfg.FirecrackerBin = v
	}
	if v := os.Getenv("KERNEL_PATH"); v != "" {
		cfg.Kernel = v
	}
	if v := os.Getenv("ROOTFS_PATH"); v != "" {
		cfg.Rootfs = v
	}
	if v := os.Getenv("AGENTFS_DATA"); v != "" {
		cfg.AgentfsData = v
	}
	if v := os.Getenv("AGENTFS_BIN"); v != "" {
		cfg.AgentfsBin = v
	}

	return cfg, nil
}

func DefaultConfig() *VirtualMachineConfig {
	return &VirtualMachineConfig{
		MaxVMs:         DefaultMaxVMs,
		AgentfsBin:     AgentfsBin,
		FirecrackerBin: FirecrackerBin,
		Rootfs:         RootfsPath,
		Kernel:         KernelPath,
		AgentfsData:    AgentfsData,
	}
}

type Formatters struct {
	TapDev    TapDevFormatter
	NFS       NFSPortFormatter
	Socket    SocketFormatter
	HostTapIP HostTapIPFormatter
	VMIP      IPFormatter
	Subnet    SubnetFormatter
}

func NewFormatters(cfg *VirtualMachineConfig) *Formatters {
	return &Formatters{
		TapDev:    TapDevFormatter{Prefix: BaseTapDev, MaxSlots: cfg.MaxVMs},
		NFS:       NFSPortFormatter{BasePort: BaseNFSPort},
		Socket:    SocketFormatter{AgentfsDirectory: cfg.AgentfsData},
		HostTapIP: HostTapIPFormatter{},
		VMIP:      IPFormatter{},
		Subnet:    SubnetFormatter{},
	}
}

var DefaultFormatters = NewFormatters(DefaultConfig())

type CreateVirtualMachineInput struct {
	ID         string `json:"id,omitempty"`
	VCPUs      int    `json:"vcpus,omitempty"`
	MemoryMib  int    `json:"memory_mib,omitempty"`
	KernelArgs string `json:"kernel_args,omitempty"`
}

func (input *CreateVirtualMachineInput) WithDefaults() *CreateVirtualMachineInput {
	input.Validate()
	return input
}

func (input *CreateVirtualMachineInput) Validate() error {
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
	return nil
}
