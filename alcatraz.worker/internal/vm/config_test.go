package vm

import (
	"testing"
)

func TestLoadConfig_DefaultsAreAbsoluteAndAnchoredToExecutable(t *testing.T) {
	t.Setenv("WORKER_FIRECRACKER_BIN", "")
	t.Setenv("WORKER_KERNEL_PATH", "")
	t.Setenv("WORKER_ROOTFS_PATH", "")
	t.Setenv("WORKER_AGENTFS_DATA", "")

	cfg := LoadConfig()

	if cfg.MaxVMs != DefaultMaxVMs {
		t.Errorf("MaxVMs = %d, want %d", cfg.MaxVMs, DefaultMaxVMs)
	}
	for _, p := range []string{cfg.FirecrackerBin, cfg.Kernel, cfg.Rootfs, cfg.AgentfsData} {
		if p == "" {
			t.Errorf("expected non-empty default path")
		}
	}
}

func TestLoadConfig_EnvOverridesWin(t *testing.T) {
	t.Setenv("WORKER_FIRECRACKER_BIN", "/custom/fc")
	t.Setenv("WORKER_KERNEL_PATH", "/custom/vmlinux")
	t.Setenv("WORKER_ROOTFS_PATH", "/custom/rootfs")
	t.Setenv("WORKER_AGENTFS_DATA", "/custom/.agentfs")

	cfg := LoadConfig()

	if cfg.FirecrackerBin != "/custom/fc" {
		t.Errorf("FirecrackerBin = %q", cfg.FirecrackerBin)
	}
	if cfg.Kernel != "/custom/vmlinux" {
		t.Errorf("Kernel = %q", cfg.Kernel)
	}
	if cfg.Rootfs != "/custom/rootfs" {
		t.Errorf("Rootfs = %q", cfg.Rootfs)
	}
	if cfg.AgentfsData != "/custom/.agentfs" {
		t.Errorf("AgentfsData = %q", cfg.AgentfsData)
	}
}

func TestValidateArtifacts_FailsWithReadableErrorWhenMissing(t *testing.T) {
	t.Setenv("WORKER_FIRECRACKER_BIN", "/nonexistent/fc")
	t.Setenv("WORKER_KERNEL_PATH", "/nonexistent/vmlinux")
	t.Setenv("WORKER_ROOTFS_PATH", "/nonexistent/rootfs")

	cfg := LoadConfig()
	err := cfg.ValidateArtifacts()
	if err == nil {
		t.Fatal("expected validation error for nonexistent paths")
	}
	if !contains(err.Error(), "WORKER_FIRECRACKER_BIN") {
		t.Errorf("expected error to mention env override; got: %v", err)
	}
}

func contains(s, substr string) bool {
	for i := 0; i+len(substr) <= len(s); i++ {
		if s[i:i+len(substr)] == substr {
			return true
		}
	}
	return false
}

func TestApplyDefaults(t *testing.T) {
	tests := []struct {
		name      string
		req       CreateVirtualMachineInput
		wantID    bool
		wantVCPUs int
		wantMem   int
	}{
		{
			name:      "empty request gets defaults",
			req:       CreateVirtualMachineInput{},
			wantID:    true,
			wantVCPUs: DefaultVCPUs,
			wantMem:   DefaultMemMib,
		},
		{
			name: "partial request",
			req: CreateVirtualMachineInput{
				VCPUs: 8,
			},
			wantVCPUs: 8,
			wantMem:   DefaultMemMib,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			tt.req.applyDefaults()
			if tt.wantID && tt.req.ID == "" {
				t.Error("expected ID to be set")
			}
			if tt.req.VCPUs != tt.wantVCPUs {
				t.Errorf("VCPUs = %d, want %d", tt.req.VCPUs, tt.wantVCPUs)
			}
			if tt.req.MemoryMib != tt.wantMem {
				t.Errorf("MemoryMib = %d, want %d", tt.req.MemoryMib, tt.wantMem)
			}
		})
	}
}

func TestConstants(t *testing.T) {
	if DefaultVCPUs <= 0 {
		t.Error("DefaultVCPUs should be positive")
	}
	if DefaultMemMib <= 0 {
		t.Error("DefaultMemMib should be positive")
	}
}
