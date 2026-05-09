package vm

import (
	"context"
	"fmt"
	"log/slog"
	"sync"

	firecracker "github.com/firecracker-microvm/firecracker-go-sdk"

	"alcatraz.worker/internal/vm/agentfs"
)

type VirtualMachine struct {
	id         string
	vcpus      int
	memoryMib  int
	kernelArgs string
	index      int

	tapDev    string
	nfsPort   int
	socket    string
	agentID   string
	hostTapIP string
	vmIP      string

	machine   *firecracker.Machine
	nfsServer *agentfs.NFSServer

	mu sync.Mutex
}

func NewVirtualMachine(options ...VirtualMachineOption) *VirtualMachine {
	vm := &VirtualMachine{}
	for _, option := range options {
		option(vm)
	}
	return vm
}

type VirtualMachineOption func(*VirtualMachine)

func WithID(id string) VirtualMachineOption {
	return func(vm *VirtualMachine) {
		vm.id = id
		vm.agentID = id
	}
}

func WithIndex(index int) VirtualMachineOption {
	return func(vm *VirtualMachine) {
		vm.index = index
		vm.tapDev = fmt.Sprintf("fc-tap%d", index)
		vm.nfsPort = NFSBasePort + index
	}
}

func WithSocket(socket string) VirtualMachineOption {
	return func(vm *VirtualMachine) {
		vm.socket = socket
	}
}

func WithInput(input *CreateVirtualMachineInput) VirtualMachineOption {
	return func(vm *VirtualMachine) {
		vm.id = input.ID
		vm.agentID = input.ID
		vm.vcpus = input.VCPUs
		vm.memoryMib = input.MemoryMib
		vm.kernelArgs = input.KernelArgs
	}
}

func (vm *VirtualMachine) GetID() string        { return vm.id }
func (vm *VirtualMachine) GetHostTapIP() string { return vm.hostTapIP }
func (vm *VirtualMachine) GetVMIP() string      { return vm.vmIP }

func (vm *VirtualMachine) SetHostTapIP(ip string) { vm.hostTapIP = ip }
func (vm *VirtualMachine) SetVMIP(ip string)      { vm.vmIP = ip }
func (vm *VirtualMachine) GetMachine() *firecracker.Machine {
	return vm.machine
}

func (vm *VirtualMachine) SetNFSServer(srv *agentfs.NFSServer) {
	vm.mu.Lock()
	defer vm.mu.Unlock()
	vm.nfsServer = srv
}

type VirtualMachineService struct {
	mu        sync.Mutex
	vms       map[string]*VirtualMachine
	pool      *IntPool
	maxVMs    int
	cleanupWG sync.WaitGroup
}

// IntPool is a small bounded pool of distinct ints in [0, maxSize). Release
// silently no-ops on a double-release rather than corrupting state.
type IntPool struct {
	mu      sync.Mutex
	items   []int
	maxSize int
}

func NewIntPool(maxSize int) *IntPool {
	items := make([]int, maxSize)
	for i := range items {
		items[i] = i
	}
	return &IntPool{items: items, maxSize: maxSize}
}

func (p *IntPool) Len() int {
	p.mu.Lock()
	defer p.mu.Unlock()
	return len(p.items)
}

func (p *IntPool) Allocate() (int, error) {
	p.mu.Lock()
	defer p.mu.Unlock()
	if len(p.items) == 0 {
		return 0, fmt.Errorf("no available slots (max %d)", p.maxSize)
	}
	index := p.items[0]
	p.items = p.items[1:]
	return index, nil
}

// Release returns an index to the pool. If the index is out of range or
// already free, the call is logged and dropped — protects against the spawn
// error-path + cleanup-goroutine both releasing the same slot.
func (p *IntPool) Release(index int) {
	p.mu.Lock()
	defer p.mu.Unlock()
	if index < 0 || index >= p.maxSize {
		slog.Warn("pool: ignoring out-of-range release", "index", index, "max", p.maxSize)
		return
	}
	for _, existing := range p.items {
		if existing == index {
			slog.Warn("pool: ignoring double-release", "index", index)
			return
		}
	}
	p.items = append(p.items, index)
}

// NewVirtualMachineService constructs the service from an explicit config so
// callers (and tests) can wire in alternate values.
func NewVirtualMachineService(cfg *VirtualMachineConfig) *VirtualMachineService {
	return &VirtualMachineService{
		vms:    make(map[string]*VirtualMachine),
		pool:   NewIntPool(cfg.MaxVMs),
		maxVMs: cfg.MaxVMs,
	}
}

func newVirtualMachineServiceWithMax(maxVMs int) *VirtualMachineService {
	return &VirtualMachineService{
		vms:    make(map[string]*VirtualMachine),
		pool:   NewIntPool(maxVMs),
		maxVMs: maxVMs,
	}
}

func (s *VirtualMachineService) Allocate() (int, error) {
	return s.pool.Allocate()
}

func (s *VirtualMachineService) Release(index int) {
	s.pool.Release(index)
}

func (s *VirtualMachineService) AddVirtualMachine(vm *VirtualMachine) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.vms[vm.id] = vm
}

func (s *VirtualMachineService) RemoveVirtualMachine(id string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.vms, id)
}

func (s *VirtualMachineService) GetMaxVMs() int {
	return s.maxVMs
}

func (s *VirtualMachineService) GetVirtualMachine(id string) *VirtualMachine {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.vms[id]
}

func (s *VirtualMachineService) ListVirtualMachines() []*VirtualMachine {
	s.mu.Lock()
	defer s.mu.Unlock()
	instances := make([]*VirtualMachine, 0, len(s.vms))
	for _, inst := range s.vms {
		instances = append(instances, inst)
	}
	return instances
}

// Destroy asks Firecracker to stop the VM with the given id. The post-exit
// cleanup goroutine wired up in Spawn handles CNI DEL, slot release, and the
// vm.destroyed publish — Destroy only needs to issue StopVMM. Returns nil for
// an unknown id (idempotent: at-least-once vm.destroy delivery may double-fire).
func (s *VirtualMachineService) Destroy(id string) error {
	instance := s.GetVirtualMachine(id)
	if instance == nil {
		return nil
	}
	machine := instance.GetMachine()
	if machine == nil {
		return nil
	}
	return machine.StopVMM()
}

// Shutdown stops every running VM and waits for the per-VM cleanup goroutines
// (which run the SDK's CNI DEL via doCleanup) to finish, so IPAM leases and TAP
// devices are released before the worker exits. Bounded by ctx — if it expires,
// any leases still on disk will leak and need to be swept on next start.
func (s *VirtualMachineService) Shutdown(ctx context.Context) {
	vms := s.ListVirtualMachines()
	if len(vms) == 0 {
		return
	}

	slog.Info("Shutting down running VMs", "count", len(vms))
	for _, vm := range vms {
		machine := vm.GetMachine()
		if machine == nil {
			continue
		}
		if err := machine.StopVMM(); err != nil {
			slog.Error("VM StopVMM error", "vm_id", vm.GetID(), "err", err)
		}
	}

	done := make(chan struct{})
	go func() {
		s.cleanupWG.Wait()
		close(done)
	}()

	select {
	case <-done:
		slog.Info("VM cleanup complete")
	case <-ctx.Done():
		slog.Warn("VM cleanup timed out — IPAM leases may need manual cleanup", "err", ctx.Err())
	}
}
