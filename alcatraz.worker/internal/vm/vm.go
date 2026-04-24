package vm

import (
	"context"
	"fmt"
	"log"

	firecracker "github.com/firecracker-microvm/firecracker-go-sdk"
	"github.com/firecracker-microvm/firecracker-go-sdk/client/models"
)

type SpawnOptions struct {
	AgentfsBin     string
	FirecrackerBin string
	Rootfs         string
	Kernel         string
	AgentfsData    string
}

type VirtualMachineBuilder struct {
	instance   *VirtualMachine
	options    *SpawnOptions
	formatters *Formatters
}

func NewVirtualMachineBuilder() *VirtualMachineBuilder {
	return &VirtualMachineBuilder{
		instance:   NewVirtualMachine(),
		options:    &SpawnOptions{},
		formatters: DefaultFormatters,
	}
}

func (builder *VirtualMachineBuilder) WithRequest(req *CreateVirtualMachineInput) *VirtualMachineBuilder {
	builder.instance.id = req.ID
	builder.instance.vcpus = req.VCPUs
	builder.instance.memoryMib = req.MemoryMib
	builder.instance.kernelArgs = req.KernelArgs
	return builder
}

func (builder *VirtualMachineBuilder) WithIndex(index int) *VirtualMachineBuilder {
	builder.instance.index = index
	builder.instance.tapDev = builder.formatters.TapDev.Format(index)
	builder.instance.hostTapIP = builder.formatters.HostTapIP.Format(index)
	builder.instance.vmIP = builder.formatters.VMIP.Format(index)
	builder.instance.subnet = builder.formatters.Subnet.Format(index)
	builder.instance.nfsPort = builder.formatters.NFS.Format(index)
	return builder
}

func (builder *VirtualMachineBuilder) WithAgentID(id string) *VirtualMachineBuilder {
	builder.instance.agentID = id
	return builder
}

func (builder *VirtualMachineBuilder) WithSpawnOptions(opts *SpawnOptions) *VirtualMachineBuilder {
	builder.options = opts
	return builder
}

func (builder *VirtualMachineBuilder) WithFormatters(f *Formatters) *VirtualMachineBuilder {
	builder.formatters = f
	return builder
}

func (builder *VirtualMachineBuilder) Build() *VirtualMachine {
	builder.instance.socket = builder.formatters.Socket.Format(builder.instance.agentID)
	return builder.instance
}

func Spawn(
	context context.Context,
	instanceManager *VirtualMachineService,
	createVMInput *CreateVirtualMachineInput,
	options *SpawnOptions) (*VirtualMachine, error) {
	index, err := instanceManager.Allocate()
	if err != nil {
		return nil, err
	}

	input := createVMInput.WithDefaults()

	instance := NewVirtualMachineBuilder().
		WithRequest(input).
		WithIndex(index).
		WithAgentID(input.ID).
		WithSpawnOptions(options).
		Build()

	log.Printf("Spawning VM %s (vCPUs: %d, Mem: %d MiB, index: %d)", instance.id, instance.vcpus, instance.memoryMib, index)

	if err := SetupTap(instance, instanceManager.GetMaxVMs()); err != nil {
		instanceManager.Release(index)
		return nil, fmt.Errorf("setup tap: %w", err)
	}

	if err := PrepareAgentfsOverlay(instance, options.AgentfsBin, options.Rootfs, options.AgentfsData); err != nil {
		CleanupInstance(instance, instanceManager.GetMaxVMs())
		instanceManager.Release(index)
		return nil, fmt.Errorf("prepare agentfs: %w", err)
	}

	nfsProc, err := StartAgentfsNFS(instance, options.AgentfsBin)
	if err != nil {
		CleanupInstance(instance, instanceManager.GetMaxVMs())
		instanceManager.Release(index)
		return nil, fmt.Errorf("start agentfs nfs: %w", err)
	}
	instance.SetNFSProcess(nfsProc)

	subnetMask := "255.255.255.0"
	bootArgs := fmt.Sprintf(
		"console=ttyS0 reboot=k panic=1 pci=off %s ip=%s::%s:%s:%s:eth0:off root=/dev/nfs nfsroot=%s:/,nfsvers=3,tcp,nolock,port=%d,mountport=%d rw init=/init",
		instance.kernelArgs,
		instance.vmIP,
		instance.hostTapIP,
		subnetMask,
		VMHostname,
		instance.hostTapIP,
		instance.nfsPort,
		instance.nfsPort,
	)

	cfg := firecracker.Config{
		SocketPath:      instance.socket,
		KernelImagePath: options.Kernel,
		KernelArgs:      bootArgs,
		MachineCfg: models.MachineConfiguration{
			VcpuCount:  firecracker.Int64(int64(instance.vcpus)),
			MemSizeMib: firecracker.Int64(int64(instance.memoryMib)),
		},
		NetworkInterfaces: []firecracker.NetworkInterface{
			{
				StaticConfiguration: &firecracker.StaticNetworkConfiguration{
					MacAddress:  GuestMAC,
					HostDevName: instance.tapDev,
				},
			},
		},
	}

	fcBinPath := options.FirecrackerBin
	if !FileExists(fcBinPath) {
		CleanupInstance(instance, instanceManager.GetMaxVMs())
		instanceManager.Release(index)
		return nil, fmt.Errorf("firecracker binary not found: %s", fcBinPath)
	}

	cmd := firecracker.VMCommandBuilder{}.
		WithBin(fcBinPath).
		WithSocketPath(instance.socket).
		Build(context)

	m, err := firecracker.NewMachine(context, cfg, firecracker.WithProcessRunner(cmd))
	if err != nil {
		CleanupInstance(instance, instanceManager.GetMaxVMs())
		instanceManager.Release(index)
		return nil, fmt.Errorf("create machine: %w", err)
	}

	if err := m.Start(context); err != nil {
		instanceManager.RemoveVirtualMachine(instance.id)
		CleanupInstance(instance, instanceManager.GetMaxVMs())
		instanceManager.Release(index)
		return nil, fmt.Errorf("start machine: %w", err)
	}

	log.Printf("VM %s started (IP: %s)", instance.id, instance.vmIP)

	go func() {
		id := instance.id
		if err := m.Wait(context); err != nil {
			log.Printf("VM %s wait error: %v", id, err)
		}
		log.Printf("VM %s exited", id)
		instanceManager.RemoveVirtualMachine(id)
		CleanupInstance(instance, instanceManager.GetMaxVMs())
		instanceManager.Release(index)
	}()

	return instance, nil
}

func StopVM(inst *VirtualMachine) error {
	if inst.machine != nil {
		return inst.machine.StopVMM()
	}
	return nil
}
