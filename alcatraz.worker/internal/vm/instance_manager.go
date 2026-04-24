package vm

import (
	"fmt"
	"sync"

	firecracker "github.com/firecracker-microvm/firecracker-go-sdk"
)

type VirtualMachine struct {
	id         string
	vcpus      int
	memoryMib int
	kernelArgs string
	index     int

	tapDev    string
	hostTapIP string
	vmIP     string
	subnet   string
	nfsPort  int
	socket   string
	agentID  string

	machine *firecracker.Machine
	nfsProc NFSProcess

	mu sync.Mutex
}

func NewVirtualMachine(options ...VirtualMachineOption) *VirtualMachine {
	virtualMachine := &VirtualMachine{}
	for _, option := range options {
		option(virtualMachine)
	}
	return virtualMachine
}

type VirtualMachineOption func(*VirtualMachine)

func WithID(id string) VirtualMachineOption {
	return func(virtualMachine *VirtualMachine) {
		virtualMachine.id = id
		virtualMachine.agentID = id
	}
}

func WithVCPUs(vcpus int) VirtualMachineOption {
	return func(virtualMachine *VirtualMachine) {
		virtualMachine.vcpus = vcpus
	}
}

func WithMemory(mib int) VirtualMachineOption {
	return func(virtualMachine *VirtualMachine) {
		virtualMachine.memoryMib = mib
	}
}

func WithKernelArgs(args string) VirtualMachineOption {
	return func(virtualMachine *VirtualMachine) {
		virtualMachine.kernelArgs = args
	}
}

func WithIndex(index int, formatters *Formatters) VirtualMachineOption {
	return func(virtualMachine *VirtualMachine) {
		virtualMachine.index = index
		virtualMachine.tapDev = formatters.TapDev.Format(index)
		virtualMachine.hostTapIP = formatters.HostTapIP.Format(index)
		virtualMachine.vmIP = formatters.VMIP.Format(index)
		virtualMachine.subnet = formatters.Subnet.Format(index)
		virtualMachine.nfsPort = formatters.NFS.Format(index)
	}
}

func WithSocket(socket string) VirtualMachineOption {
	return func(virtualMachine *VirtualMachine) {
		virtualMachine.socket = socket
	}
}

func WithMachine(machine *firecracker.Machine) VirtualMachineOption {
	return func(virtualMachine *VirtualMachine) {
		virtualMachine.machine = machine
	}
}

func WithNFSProcess(process NFSProcess) VirtualMachineOption {
	return func(virtualMachine *VirtualMachine) {
		virtualMachine.nfsProc = process
	}
}

func (virtualMachine *VirtualMachine) GetID() string         { return virtualMachine.id }
func (virtualMachine *VirtualMachine) GetVCPUs() int         { return virtualMachine.vcpus }
func (virtualMachine *VirtualMachine) GetMemoryMib() int     { return virtualMachine.memoryMib }
func (virtualMachine *VirtualMachine) GetKernelArgs() string { return virtualMachine.kernelArgs }
func (virtualMachine *VirtualMachine) GetIndex() int         { return virtualMachine.index }
func (virtualMachine *VirtualMachine) GetTapDev() string     { return virtualMachine.tapDev }
func (virtualMachine *VirtualMachine) GetHostTapIP() string  { return virtualMachine.hostTapIP }
func (virtualMachine *VirtualMachine) GetVMIP() string       { return virtualMachine.vmIP }
func (virtualMachine *VirtualMachine) GetSubnet() string     { return virtualMachine.subnet }
func (virtualMachine *VirtualMachine) GetNFSPort() int       { return virtualMachine.nfsPort }
func (virtualMachine *VirtualMachine) GetSocket() string     { return virtualMachine.socket }
func (virtualMachine *VirtualMachine) GetAgentID() string    { return virtualMachine.agentID }
func (virtualMachine *VirtualMachine) GetMachine() *firecracker.Machine {
	return virtualMachine.machine
}
func (virtualMachine *VirtualMachine) GetNFSProcess() NFSProcess { return virtualMachine.nfsProc }

func (virtualMachine *VirtualMachine) SetNFSProcess(proc NFSProcess) {
	virtualMachine.mu.Lock()
	defer virtualMachine.mu.Unlock()
	virtualMachine.nfsProc = proc
}

type VirtualMachineService struct {
	mu              sync.Mutex
	virtualMachines map[string]*VirtualMachine
	pool            IntPool
	maxVMs          int
}

type IntPool struct {
	items []int
	mu    sync.Mutex
}

func NewIntPool(maxSize int) IntPool {
	pool := make([]int, maxSize)
	for i := range pool {
		pool[i] = i
	}
	return IntPool{items: pool}
}

func (pool *IntPool) Len() int {
	pool.mu.Lock()
	defer pool.mu.Unlock()
	return len(pool.items)
}

func (pool *IntPool) Allocate() (int, error) {
	pool.mu.Lock()
	defer pool.mu.Unlock()
	if len(pool.items) == 0 {
		return 0, fmt.Errorf("no available slots")
	}
	index := pool.items[0]
	pool.items = pool.items[1:]
	return index, nil
}

func (pool *IntPool) Release(index int) {
	pool.mu.Lock()
	defer pool.mu.Unlock()
	pool.items = append(pool.items, index)
}

func NewVirtualMachineService(maxVMs int) *VirtualMachineService {
	return &VirtualMachineService{
		virtualMachines: make(map[string]*VirtualMachine),
		pool:            NewIntPool(maxVMs),
		maxVMs:          maxVMs,
	}
}

func (virtualMachineService *VirtualMachineService) Allocate() (int, error) {
	virtualMachineService.mu.Lock()
	defer virtualMachineService.mu.Unlock()
	if len(virtualMachineService.pool.items) == 0 {
		return 0, fmt.Errorf("no available VM slots (max %d)", virtualMachineService.maxVMs)
	}
	index := virtualMachineService.pool.items[0]
	virtualMachineService.pool.items = virtualMachineService.pool.items[1:]
	return index, nil
}

func (virtualMachineService *VirtualMachineService) Release(index int) {
	virtualMachineService.mu.Lock()
	defer virtualMachineService.mu.Unlock()
	virtualMachineService.pool.items = append(virtualMachineService.pool.items, index)
}

func (virtualMachineService *VirtualMachineService) AddVirtualMachine(virtualMachine *VirtualMachine) {
	virtualMachineService.mu.Lock()
	defer virtualMachineService.mu.Unlock()
	virtualMachineService.virtualMachines[virtualMachine.id] = virtualMachine
}

func (virtualMachineService *VirtualMachineService) RemoveVirtualMachine(id string) {
	virtualMachineService.mu.Lock()
	defer virtualMachineService.mu.Unlock()
	delete(virtualMachineService.virtualMachines, id)
}

func (virtualMachineService *VirtualMachineService) GetMaxVMs() int {
	return virtualMachineService.maxVMs
}

func (virtualMachineService *VirtualMachineService) GetVirtualMachine(id string) *VirtualMachine {
	virtualMachineService.mu.Lock()
	defer virtualMachineService.mu.Unlock()
	return virtualMachineService.virtualMachines[id]
}

func (virtualMachineService *VirtualMachineService) ListVirtualMachines() []*VirtualMachine {
	virtualMachineService.mu.Lock()
	defer virtualMachineService.mu.Unlock()
	instances := make([]*VirtualMachine, 0, len(virtualMachineService.virtualMachines))
	for _, inst := range virtualMachineService.virtualMachines {
		instances = append(instances, inst)
	}
	return instances
}
