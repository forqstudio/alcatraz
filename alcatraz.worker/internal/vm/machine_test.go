package vm

import (
	"testing"
)

func TestNewInstanceManager(t *testing.T) {
	mgr := newVirtualMachineServiceWithMax(3)
	if mgr == nil {
		t.Fatal("NewInstanceManager returned nil")
	}
	if mgr.maxVMs != 3 {
		t.Errorf("expected maxVMs 3, got %d", mgr.maxVMs)
	}
	if mgr.pool.Len() != 3 {
		t.Errorf("expected pool size 3, got %d", mgr.pool.Len())
	}
}

func TestInstanceManagerAllocate(t *testing.T) {
	mgr := newVirtualMachineServiceWithMax(3)

	firstIndex, err := mgr.Allocate()
	if err != nil {
		t.Fatalf("Allocate failed: %v", err)
	}
	if firstIndex < 0 || firstIndex >= 3 {
		t.Errorf("expected index 0-2, got %d", firstIndex)
	}

	secondIndex, err := mgr.Allocate()
	if err != nil {
		t.Fatalf("Allocate failed: %v", err)
	}
	if secondIndex == firstIndex {
		t.Errorf("expected different index, got %d", secondIndex)
	}
}

func TestInstanceManagerAllocateExhausted(t *testing.T) {
	mgr := newVirtualMachineServiceWithMax(2)

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
	mgr := newVirtualMachineServiceWithMax(2)

	firstIndex, _ := mgr.Allocate()
	if firstIndex != 0 {
		t.Errorf("expected first allocation to be 0, got %d", firstIndex)
	}

	mgr.Release(firstIndex)

	secondIndex, err := mgr.Allocate()
	if err != nil {
		t.Fatalf("Allocate after release failed: %v", err)
	}
	if secondIndex != 1 {
		t.Errorf("expected second allocation to be 1, got %d", secondIndex)
	}
}

func TestInstanceManagerAddRemove(t *testing.T) {
	mgr := newVirtualMachineServiceWithMax(5)

	inst := NewVirtualMachine(WithID("test-vm-1"))
	mgr.AddVirtualMachine(inst)

	if mgr.GetVirtualMachine("test-vm-1") == nil {
		t.Error("expected to find instance after AddInstance")
	}

	mgr.RemoveVirtualMachine("test-vm-1")
	if mgr.GetVirtualMachine("test-vm-1") != nil {
		t.Error("expected instance to be nil after RemoveInstance")
	}
}