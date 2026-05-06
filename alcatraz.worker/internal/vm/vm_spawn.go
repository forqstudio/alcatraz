package vm

import (
	"context"
	"fmt"
	"log"
	"time"

	firecracker "github.com/firecracker-microvm/firecracker-go-sdk"
	"github.com/firecracker-microvm/firecracker-go-sdk/client/models"
)

type SpawnOptions struct {
	FirecrackerBin string
	Rootfs         string
	Kernel         string
	AgentfsData    string
}

type VirtualMachineBuilder struct {
	instance *VirtualMachine
	options  *SpawnOptions
}

func NewVirtualMachineBuilder() *VirtualMachineBuilder {
	return &VirtualMachineBuilder{
		instance: NewVirtualMachine(),
		options:  &SpawnOptions{},
	}
}

func (builder *VirtualMachineBuilder) WithInput(input *CreateVirtualMachineInput) *VirtualMachineBuilder {
	builder.instance.id = input.ID
	builder.instance.vcpus = input.VCPUs
	builder.instance.memoryMib = input.MemoryMib
	builder.instance.kernelArgs = input.KernelArgs
	return builder
}

func (builder *VirtualMachineBuilder) WithIndex(index int) *VirtualMachineBuilder {
	builder.instance.index = index
	builder.instance.nfsPort = 8000 + index
	return builder
}

func (builder *VirtualMachineBuilder) WithAgentID(id string) *VirtualMachineBuilder {
	builder.instance.agentID = id
	return builder
}

func (builder *VirtualMachineBuilder) WithSpawnOptions(spawnOptions *SpawnOptions) *VirtualMachineBuilder {
	builder.options = spawnOptions
	return builder
}

func (builder *VirtualMachineBuilder) Build() *VirtualMachine {
	builder.instance.socket = fmt.Sprintf("/tmp/alcatraz-%s.sock", builder.instance.agentID)
	return builder.instance
}

func Spawn(
	ctx context.Context,
	virtualMachineService *VirtualMachineService,
	createVMInput *CreateVirtualMachineInput,
	spawnOptions *SpawnOptions) (*VirtualMachine, error) {
	index, err := virtualMachineService.Allocate()
	if err != nil {
		return nil, err
	}

	input := createVMInput.WithDefaults()

	tapDev := fmt.Sprintf("fc-tap%d", index)

	instance := NewVirtualMachineBuilder().
		WithInput(input).
		WithIndex(index).
		WithAgentID(input.ID).
		WithSpawnOptions(spawnOptions).
		Build()

	instance.tapDev = tapDev
	log.Printf("Spawning VM %s (vCPUs: %d, Mem: %d MiB, index: %d)", instance.id, instance.vcpus, instance.memoryMib, index)

	if err := PrepareAgentfsOverlay(ctx, instance, spawnOptions.Rootfs, spawnOptions.AgentfsData); err != nil {
		virtualMachineService.Release(index)
		return nil, fmt.Errorf("prepare agentfs: %w", err)
	}

	// Determine gateway IP (NFS server address)
	// From CNI host-local IPAM, the gateway is the first IP in the subnet (172.16.0.1 for 172.16.0.0/24)
	gatewayIP := "172.16.0.1"
	instance.SetHostTapIP(gatewayIP)

	// NFS will be started in a custom handler after CNI sets up the network
	// (the alcatraz0 bridge needs to exist before NFS can bind to 172.16.0.1)

	// Boot args for NFS root mount
	// The "ip=" parameter will be added by the SDK's SetupKernelArgs handler based on CNI results
	bootArgs := fmt.Sprintf(
		"console=ttyS0 reboot=k panic=1 pci=off %s root=/dev/nfs nfsroot=%s:/,nfsvers=3,tcp,nolock,port=%d,mountport=%d rw init=/init",
		instance.kernelArgs,
		gatewayIP,
		instance.nfsPort,
		instance.nfsPort,
	)

	cfg := firecracker.Config{
		SocketPath:      instance.socket,
		KernelImagePath: spawnOptions.Kernel,
		KernelArgs:      bootArgs,
		MachineCfg: models.MachineConfiguration{
			VcpuCount:  firecracker.Int64(int64(instance.vcpus)),
			MemSizeMib: firecracker.Int64(int64(instance.memoryMib)),
		},
		NetworkInterfaces: []firecracker.NetworkInterface{
			{
				CNIConfiguration: &firecracker.CNIConfiguration{
					NetworkName: "alcatraz-bridge",
					IfName:      tapDev,
					VMIfName:    "eth0",
					ConfDir:     "/etc/cni/net.d",
					BinPath:     []string{"/opt/cni/bin"},
				},
			},
		},
		VMID: instance.id,
	}

	firecrackerBinPath := spawnOptions.FirecrackerBin
	if !FileExists(firecrackerBinPath) {
		virtualMachineService.Release(index)
		return nil, fmt.Errorf("firecracker binary not found: %s", firecrackerBinPath)
	}

	cmd := firecracker.VMCommandBuilder{}.
		WithBin(firecrackerBinPath).
		WithSocketPath(instance.socket).
		Build(ctx)

	m, err := firecracker.NewMachine(ctx, cfg, firecracker.WithProcessRunner(cmd),
		func(m *firecracker.Machine) {
			// Add a custom handler to start NFS after CNI sets up the network
			m.Handlers.FcInit = m.Handlers.FcInit.AppendAfter(
				firecracker.SetupNetworkHandlerName,
				firecracker.Handler{
					Name: "alcatraz.StartNFS",
					Fn: func(ctx context.Context, m *firecracker.Machine) error {
						nfsProc, err := StartAgentfsNFS(ctx, instance, spawnOptions.Rootfs, spawnOptions.AgentfsData)
						if err != nil {
							return fmt.Errorf("start agentfs nfs: %w", err)
						}
						instance.SetNFSProcess(nfsProc)
						return nil
					},
				},
			)
		},
	)
	if err != nil {
		virtualMachineService.Release(index)
		return nil, fmt.Errorf("create machine: %w", err)
	}

	if err := m.Start(ctx); err != nil {
		virtualMachineService.RemoveVirtualMachine(instance.id)
		virtualMachineService.Release(index)
		return nil, fmt.Errorf("start machine: %w", err)
	}

	// Extract IPs from CNI result via the machine's network interfaces
	for _, iface := range m.Cfg.NetworkInterfaces {
		if iface.StaticConfiguration != nil && iface.StaticConfiguration.IPConfiguration != nil {
			ipConf := iface.StaticConfiguration.IPConfiguration
			instance.SetVMIP(ipConf.IPAddr.String())
			instance.SetHostTapIP(ipConf.Gateway.String())
			break
		}
	}

	log.Printf("VM %s started (tap: %s, IP: %s, gateway: %s)",
		instance.id, instance.tapDev, instance.GetVMIP(), instance.GetHostTapIP())

	go func() {
		id := instance.id
		// Use background context so cleanup isn't cancelled when Spawn() returns
		waitCtx := context.Background()
		if err := m.Wait(waitCtx); err != nil {
			log.Printf("VM %s wait error: %v", id, err)
		}
		log.Printf("VM %s exited, SDK should now run doCleanup() to release CNI resources", id)

		// Give SDK time to run doCleanup()
		time.Sleep(2 * time.Second)

		log.Printf("VM %s cleanup complete, removing from service", id)
		virtualMachineService.RemoveVirtualMachine(id)
		virtualMachineService.Release(index)
	}()

	return instance, nil
}

func StopVM(virtualMachine *VirtualMachine) error {
	if virtualMachine.machine != nil {
		return virtualMachine.machine.StopVMM()
	}
	return nil
}
