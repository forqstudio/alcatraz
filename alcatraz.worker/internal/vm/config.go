package vm

import (
	"fmt"
	"os"
	"path/filepath"

	"github.com/google/uuid"
)

const (
	DefaultMaxVMs = 5

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

	firecrackerRel = "bin/firecracker-v1.15.1"
	kernelRel      = "linux-amazon/vmlinux"
	rootfsRel      = "rootfs"
	agentfsDataRel = ".agentfs"
)

type VirtualMachineConfig struct {
	MaxVMs         int
	CNIConfDir     string
	AgentfsData    string
	FirecrackerBin string
	Rootfs         string
	Kernel         string
}

// LoadConfig builds the VM config. Path defaults are derived from the
// executable's location (so CWD doesn't matter), then optionally overridden by
// env vars. ValidateArtifacts must be called separately to lstat the resolved
// paths.
func LoadConfig() *VirtualMachineConfig {
	core := defaultCoreDir()
	cfg := &VirtualMachineConfig{
		MaxVMs:         DefaultMaxVMs,
		CNIConfDir:     CNIConfDir,
		FirecrackerBin: filepath.Join(core, firecrackerRel),
		Kernel:         filepath.Join(core, kernelRel),
		Rootfs:         filepath.Join(core, rootfsRel),
		AgentfsData:    filepath.Join(core, agentfsDataRel),
	}
	if v := os.Getenv("WORKER_FIRECRACKER_BIN"); v != "" {
		cfg.FirecrackerBin = v
	}
	if v := os.Getenv("WORKER_KERNEL_PATH"); v != "" {
		cfg.Kernel = v
	}
	if v := os.Getenv("WORKER_ROOTFS_PATH"); v != "" {
		cfg.Rootfs = v
	}
	if v := os.Getenv("WORKER_AGENTFS_DATA"); v != "" {
		cfg.AgentfsData = v
	}
	return cfg
}

// ValidateArtifacts lstats the read-only inputs the worker can't run without.
// AgentfsData is intentionally not checked — the worker creates it on demand.
func (c *VirtualMachineConfig) ValidateArtifacts() error {
	checks := []struct {
		label, path, env string
	}{
		{"firecracker binary", c.FirecrackerBin, "WORKER_FIRECRACKER_BIN"},
		{"kernel", c.Kernel, "WORKER_KERNEL_PATH"},
		{"rootfs", c.Rootfs, "WORKER_ROOTFS_PATH"},
	}
	for _, chk := range checks {
		if _, err := os.Stat(chk.path); err != nil {
			return fmt.Errorf("%s not found at %q (override with %s in env or .env): %w",
				chk.label, chk.path, chk.env, err)
		}
	}
	return nil
}

// defaultCoreDir returns <repo>/alcatraz.core, derived from the executable's
// location. Binary lives at <repo>/alcatraz.worker/bin/alcatraz-worker, so the
// core dir is the executable's grandparent's sibling. Falls back to the
// legacy CWD-relative ../alcatraz.core if os.Executable fails (which would be
// a very unusual host condition).
func defaultCoreDir() string {
	exe, err := os.Executable()
	if err != nil {
		return "../alcatraz.core"
	}
	if resolved, err := filepath.EvalSymlinks(exe); err == nil {
		exe = resolved
	}
	// exe = .../alcatraz.worker/bin/alcatraz-worker
	// Dir(Dir(exe)) = .../alcatraz.worker
	// Its sibling = .../alcatraz.core
	workerDir := filepath.Dir(filepath.Dir(exe))
	return filepath.Join(filepath.Dir(workerDir), "alcatraz.core")
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
