package vm

import (
	"context"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"

	"alcatraz.worker/internal/vm/agentfs"
	firecracker "github.com/firecracker-microvm/firecracker-go-sdk"
	"github.com/firecracker-microvm/firecracker-go-sdk/client/models"
)

type SpawnOptions struct {
	FirecrackerBin string
	Rootfs         string
	Kernel         string
	AgentfsData    string
}

func Spawn(
	ctx context.Context,
	s *VirtualMachineService,
	createVMInput *CreateVirtualMachineInput,
	spawnOptions *SpawnOptions) (*VirtualMachine, error) {
	index, err := s.Allocate()
	if err != nil {
		return nil, err
	}

	createVMInput.applyDefaults()

	instance := NewVirtualMachine(
		WithInput(createVMInput),
		WithIndex(index),
		WithSocket(fmt.Sprintf("/tmp/alcatraz-%s.sock", createVMInput.ID)),
	)

	// Track success so the deferred cleanup runs only on the failure path.
	success := false
	defer func() {
		if success {
			return
		}
		if instance.nfsServer != nil {
			_ = instance.nfsServer.Kill()
		}
		removeOverlayFiles(spawnOptions.AgentfsData, instance.agentID)
		s.Release(index)
	}()

	slog.Info("Spawning VM",
		"vm_id", instance.id,
		"vcpus", instance.vcpus,
		"memory_mib", instance.memoryMib,
		"index", index,
	)
	slog.Info("VM allocated slot",
		"vm_id", instance.id,
		"slot", index,
		"tap", instance.tapDev,
		"nfs_port", instance.nfsPort,
	)

	if err := agentfs.PrepareOverlay(ctx, instance.agentID, spawnOptions.Rootfs, spawnOptions.AgentfsData); err != nil {
		return nil, fmt.Errorf("prepare agentfs: %w", err)
	}
	slog.Info("VM agentfs overlay ready",
		"vm_id", instance.id,
		"data", spawnOptions.AgentfsData,
	)

	// The CNI host-local IPAM gateway is the first IP in SubnetCIDR. We set it
	// before m.Start so the AppendAfter(SetupNetwork) handler below can read
	// instance.hostTapIP when it binds the NFS listener. The post-Start block
	// re-reads the gateway from the CNI result so the value is authoritative.
	instance.SetHostTapIP(GatewayIP)

	bootArgs := fmt.Sprintf(
		"console=ttyS0 reboot=k panic=1 pci=off %s root=/dev/nfs nfsroot=%s:/,nfsvers=3,tcp,nolock,port=%d,mountport=%d rw init=/init",
		instance.kernelArgs,
		GatewayIP,
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
					NetworkName: BridgeName,
					IfName:      instance.tapDev,
					VMIfName:    "eth0",
					ConfDir:     CNIConfDir,
					BinPath:     []string{CNIBinDir},
				},
			},
		},
		VMID: instance.id,
	}

	if _, err := os.Stat(spawnOptions.FirecrackerBin); err != nil {
		return nil, fmt.Errorf("firecracker binary not found: %s: %w", spawnOptions.FirecrackerBin, err)
	}

	cmd := firecracker.VMCommandBuilder{}.
		WithBin(spawnOptions.FirecrackerBin).
		WithSocketPath(instance.socket).
		Build(ctx)

	m, err := firecracker.NewMachine(ctx, cfg, firecracker.WithProcessRunner(cmd),
		func(m *firecracker.Machine) {
			m.Handlers.FcInit = m.Handlers.FcInit.AppendAfter(
				firecracker.SetupNetworkHandlerName,
				firecracker.Handler{
					Name: "alcatraz.StartNFS",
					Fn: func(ctx context.Context, m *firecracker.Machine) error {
						slog.Info("VM network up, starting AgentFS NFS server", "vm_id", instance.id)
						srv, err := agentfs.OpenAndServe(ctx, instance.agentID, instance.hostTapIP, instance.nfsPort, spawnOptions.Rootfs, spawnOptions.AgentfsData)
						if err != nil {
							return fmt.Errorf("start agentfs nfs: %w", err)
						}
						instance.SetNFSServer(srv)
						return nil
					},
				},
			)
		},
	)
	if err != nil {
		return nil, fmt.Errorf("create machine: %w", err)
	}

	slog.Info("VM booting Firecracker",
		"vm_id", instance.id,
		"tap", instance.tapDev,
		"bridge", BridgeName,
	)
	if err := m.Start(ctx); err != nil {
		return nil, fmt.Errorf("start machine: %w", err)
	}

	instance.machine = m
	s.AddVirtualMachine(instance)

	for _, iface := range m.Cfg.NetworkInterfaces {
		if iface.StaticConfiguration != nil && iface.StaticConfiguration.IPConfiguration != nil {
			ipConf := iface.StaticConfiguration.IPConfiguration
			instance.SetVMIP(ipConf.IPAddr.String())
			instance.SetHostTapIP(ipConf.Gateway.String())
			break
		}
	}

	slog.Info("VM ready",
		"vm_id", instance.id,
		"tap", instance.tapDev,
		"vm_ip", instance.GetVMIP(),
		"gateway", instance.GetHostTapIP(),
		"nfs_port", instance.nfsPort,
	)

	s.cleanupWG.Add(1)
	go func() {
		defer s.cleanupWG.Done()
		id := instance.id
		// Background context: this outlives the Spawn caller's ctx. m.Wait
		// blocks until the firecracker process has exited and the SDK's
		// FcCleanup handler chain (which runs CNI DEL via doCleanup) has
		// completed, so by the time we Release the slot here, the IP/TAP
		// lease is already gone.
		if err := m.Wait(context.Background()); err != nil {
			slog.Error("VM wait error", "vm_id", id, "err", err)
		}
		s.RemoveVirtualMachine(id)
		s.Release(index)
		slog.Info("VM exited and slot released", "vm_id", id, "slot", index)
	}()

	success = true
	return instance, nil
}

// removeOverlayFiles deletes the per-agent overlay artefacts created by
// agentfs.PrepareOverlay. Errors are best-effort — if files don't exist (e.g.
// PrepareOverlay never ran), the os.Remove calls return *PathError
// which we discard.
func removeOverlayFiles(dataDir, agentID string) {
	dbPath := filepath.Join(dataDir, agentID+".db")
	for _, p := range []string{dbPath, dbPath + "-wal", dbPath + "-shm", filepath.Join(dataDir, agentID+".base-stamp")} {
		_ = os.Remove(p)
	}
}
