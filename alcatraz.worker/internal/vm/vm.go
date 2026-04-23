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

func Spawn(
	context context.Context,
	instanceManager *InstanceManager,
	request *Request,
	options *SpawnOptions) (*Instance, error) {
	index, err := instanceManager.Allocate()
	if err != nil {
		return nil, err
	}

	validatedReq := request.WithDefaults()

	instance := &Instance{
		ID:         validatedReq.ID,
		VCPUs:      validatedReq.VCPUs,
		MemoryMib:  validatedReq.MemoryMib,
		KernelArgs: validatedReq.KernelArgs,
		Index:      index,
		TapDev:     FormatTapDev(index),
		HostTapIP:  FormatHostTapIP(index),
		VMIP:       FormatVMIP(index),
		Subnet:     FormatSubnet(index),
		NFSPort:    FormatNFSPort(index),
		Socket:     FormatSocket(options.AgentfsData, validatedReq.ID),
		AgentID:    validatedReq.ID,
	}

	log.Printf("Spawning VM %s (vCPUs: %d, Mem: %d MiB, idx: %d)", instance.ID, instance.VCPUs, instance.MemoryMib, index)

	if err := SetupTap(instance); err != nil {
		instanceManager.Release(index)
		return nil, fmt.Errorf("setup tap: %w", err)
	}

	if err := SetupNAT(instance); err != nil {
		CleanupInstance(instance)
		instanceManager.Release(index)
		return nil, fmt.Errorf("setup nat: %w", err)
	}

	if err := PrepareAgentfsOverlay(instance, options.AgentfsBin, options.Rootfs, options.AgentfsData); err != nil {
		CleanupInstance(instance)
		instanceManager.Release(index)
		return nil, fmt.Errorf("prepare agentfs: %w", err)
	}

	if err := StartAgentfsNFS(instance, options.AgentfsBin); err != nil {
		CleanupInstance(instance)
		instanceManager.Release(index)
		return nil, fmt.Errorf("start agentfs nfs: %w", err)
	}

	subnetMask := "255.255.255.0"
	bootArgs := fmt.Sprintf(
		"console=ttyS0 reboot=k panic=1 pci=off %s ip=%s::%s:%s:%s:eth0:off root=/dev/nfs nfsroot=%s:/,nfsvers=3,tcp,nolock,port=%d,mountport=%d rw init=/init",
		instance.KernelArgs,
		instance.VMIP,
		instance.HostTapIP,
		subnetMask,
		VMHostname,
		instance.HostTapIP,
		instance.NFSPort,
		instance.NFSPort,
	)

	cfg := firecracker.Config{
		SocketPath:      instance.Socket,
		KernelImagePath: options.Kernel,
		KernelArgs:      bootArgs,
		MachineCfg: models.MachineConfiguration{
			VcpuCount:  firecracker.Int64(int64(instance.VCPUs)),
			MemSizeMib: firecracker.Int64(int64(instance.MemoryMib)),
		},
		NetworkInterfaces: []firecracker.NetworkInterface{
			{
				StaticConfiguration: &firecracker.StaticNetworkConfiguration{
					MacAddress:  GuestMAC,
					HostDevName: instance.TapDev,
				},
			},
		},
	}

	fcBinPath := options.FirecrackerBin
	if !FileExists(fcBinPath) {
		CleanupInstance(instance)
		instanceManager.Release(index)
		return nil, fmt.Errorf("firecracker binary not found: %s", fcBinPath)
	}

	cmd := firecracker.VMCommandBuilder{}.
		WithBin(fcBinPath).
		WithSocketPath(instance.Socket).
		Build(context)

	m, err := firecracker.NewMachine(context, cfg, firecracker.WithProcessRunner(cmd))
	if err != nil {
		CleanupInstance(instance)
		instanceManager.Release(index)
		return nil, fmt.Errorf("create machine: %w", err)
	}

	instance.Machine = m
	instance.Config = cfg

	instanceManager.AddInstance(instance)

	if err := m.Start(context); err != nil {
		instanceManager.RemoveInstance(instance.ID)
		CleanupInstance(instance)
		instanceManager.Release(index)
		return nil, fmt.Errorf("start machine: %w", err)
	}

	log.Printf("VM %s started (IP: %s)", instance.ID, instance.VMIP)

	go func() {
		id := instance.ID
		if err := m.Wait(context); err != nil {
			log.Printf("VM %s wait error: %v", id, err)
		}
		log.Printf("VM %s exited", id)
		instanceManager.RemoveInstance(id)
		CleanupInstance(instance)
		instanceManager.Release(index)
	}()

	return instance, nil
}

func StopVM(inst *Instance) error {
	if inst.Machine != nil {
		return inst.Machine.StopVMM()
	}
	return nil
}