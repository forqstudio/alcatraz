package vm

import (
	"testing"

	"alcatraz.worker/internal/config"
)

func TestFormatHostTapIP(t *testing.T) {
	tests := []struct {
		idx    int
		wantIP string
	}{
		{0, "172.16.0.1"},
		{1, "172.16.1.1"},
		{5, "172.16.5.1"},
	}

	for _, tt := range tests {
		t.Run("", func(t *testing.T) {
			got := FormatHostTapIP(tt.idx)
			if got != tt.wantIP {
				t.Errorf("FormatHostTapIP(%d) = %s, want %s", tt.idx, got, tt.wantIP)
			}
		})
	}
}

func TestFormatVMIP(t *testing.T) {
	tests := []struct {
		idx    int
		wantIP string
	}{
		{0, "172.16.0.2"},
		{1, "172.16.1.2"},
		{5, "172.16.5.2"},
	}

	for _, tt := range tests {
		t.Run("", func(t *testing.T) {
			got := FormatVMIP(tt.idx)
			if got != tt.wantIP {
				t.Errorf("FormatVMIP(%d) = %s, want %s", tt.idx, got, tt.wantIP)
			}
		})
	}
}

func TestFormatSubnet(t *testing.T) {
	tests := []struct {
		idx  int
		want string
	}{
		{0, "172.16.0.0/24"},
		{1, "172.16.1.0/24"},
		{10, "172.16.10.0/24"},
	}

	for _, tt := range tests {
		t.Run("", func(t *testing.T) {
			got := FormatSubnet(tt.idx)
			if got != tt.want {
				t.Errorf("FormatSubnet(%d) = %s, want %s", tt.idx, got, tt.want)
			}
		})
	}
}

func TestFormatNFSPort(t *testing.T) {
	basePort := config.BaseNFSPort
	for i := 0; i < 5; i++ {
		got := FormatNFSPort(i)
		want := basePort + i
		if got != want {
			t.Errorf("FormatNFSPort(%d) = %d, want %d", i, got, want)
		}
	}
}

func TestFormatTapDev(t *testing.T) {
	for i := 0; i < 5; i++ {
		got := FormatTapDev(i)
		want := "fc-tap" + string(rune('0'+i))
		if i > 9 {
			want = "fc-tap" + string(rune('0'+i/10)) + string(rune('0'+i%10))
		}
		if got != want {
			t.Errorf("FormatTapDev(%d) = %s, want %s", i, got, want)
		}
	}
}

func TestFileExists(t *testing.T) {
	if FileExists("/nonexistent/path/to/file") {
		t.Error("FileExists should return false for non-existent file")
	}

	if !FileExists("/proc/1/cmdline") {
		t.Error("FileExists should return true for existing file")
	}
}

func TestNewInstanceManager(t *testing.T) {
	mgr := NewInstanceManager(3)
	if mgr == nil {
		t.Fatal("NewInstanceManager returned nil")
	}
	if mgr.maxVMs != 3 {
		t.Errorf("expected maxVMs 3, got %d", mgr.maxVMs)
	}
	if len(mgr.pool) != 3 {
		t.Errorf("expected pool size 3, got %d", len(mgr.pool))
	}
}

func TestInstanceManagerAllocate(t *testing.T) {
	mgr := NewInstanceManager(3)

	idx, err := mgr.Allocate()
	if err != nil {
		t.Fatalf("Allocate failed: %v", err)
	}
	if idx < 0 || idx >= 3 {
		t.Errorf("expected index 0-2, got %d", idx)
	}

	idx2, err := mgr.Allocate()
	if err != nil {
		t.Fatalf("Allocate failed: %v", err)
	}
	if idx2 == idx {
		t.Errorf("expected different index, got %d", idx2)
	}
}

func TestInstanceManagerAllocateExhausted(t *testing.T) {
	mgr := NewInstanceManager(2)

	_, err := mgr.Allocate()
	if err != nil {
		t.Fatalf("Allocate failed: %v", err)
	}
	_, err = mgr.Allocate()
	if err != nil {
		t.Fatalf("Allocate failed: %v", err)
	}

	_, err = mgr.Allocate()
	if err == nil {
		t.Fatal("expected error when pool exhausted")
	}
}

func TestInstanceManagerRelease(t *testing.T) {
	mgr := NewInstanceManager(2)

	idx1, _ := mgr.Allocate()
	if idx1 != 0 {
		t.Errorf("expected first allocation to be 0, got %d", idx1)
	}

	mgr.Release(idx1)

	idx2, err := mgr.Allocate()
	if err != nil {
		t.Fatalf("Allocate after release failed: %v", err)
	}
	if idx2 != 1 {
		t.Errorf("expected second allocation to be 1, got %d", idx2)
	}
}

func TestInstanceManagerAddRemove(t *testing.T) {
	mgr := NewInstanceManager(5)

	inst := &Instance{ID: "test-vm-1"}
	mgr.AddInstance(inst)

	if mgr.GetInstance("test-vm-1") == nil {
		t.Error("expected to find instance after AddInstance")
	}

	mgr.RemoveInstance("test-vm-1")
	if mgr.GetInstance("test-vm-1") != nil {
		t.Error("expected instance to be nil after RemoveInstance")
	}
}
