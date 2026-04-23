package config

import "github.com/google/uuid"

const (
	DefaultNATSURL    = "nats://localhost:4222"
	DefaultSubject    = "vm.spawn"
	DefaultMaxVMs     = 5
	DefaultQueueGroup = "vm-workers"

	FirecrackerBin = "../alcatraz.core/bin/firecracker-v1.15.1"
	KernelPath     = "../alcatraz.core/linux-amazon/vmlinux"
	RootfsPath    = "../alcatraz.core/rootfs"
	AgentfsDir    = "../alcatraz.core/.agentfs"

	BaseTapDev    = "fc-tap"
	BaseHostTapIP = "172.16.0.1"
	BaseVMIP      = "172.16.0.2"
	BaseNFSPort   = 11111

	VMHostname = "alcatraz"
	GuestMAC   = "AA:FC:00:00:00:01"

	DefaultVCPUs     = 4
	DefaultMemMib    = 8192
	DefaultKernelArgs = "loglevel=7 printk.devkmsg=on"
)

type Config struct {
	NATSURL      string
	Subject      string
	MaxVMs       int
	QueueGroup   string

	AgentfsBin   string
	FirecrackerBin string
	Rootfs       string
	Kernel       string

	AgentfsDir   string
}

func DefaultConfig() *Config {
	return &Config{
		NATSURL:        DefaultNATSURL,
		Subject:        DefaultSubject,
		MaxVMs:         DefaultMaxVMs,
		QueueGroup:     DefaultQueueGroup,
		FirecrackerBin: FirecrackerBin,
		Kernel:         KernelPath,
		Rootfs:        RootfsPath,
		AgentfsDir:     AgentfsDir,
	}
}

type VMRequest struct {
	ID          string `json:"id,omitempty"`
	VCPUs      int    `json:"vcpus,omitempty"`
	MemoryMib  int    `json:"memory_mib,omitempty"`
	KernelArgs string `json:"kernel_args,omitempty"`
}

func (r *VMRequest) Validate() error {
	if r.ID == "" {
		r.ID = uuid.New().String()
	}
	if r.VCPUs <= 0 {
		r.VCPUs = DefaultVCPUs
	}
	if r.MemoryMib <= 0 {
		r.MemoryMib = DefaultMemMib
	}
	if r.KernelArgs == "" {
		r.KernelArgs = DefaultKernelArgs
	}
	return nil
}

func (r *VMRequest) WithDefaults() *VMRequest {
	r.Validate()
	return r
}