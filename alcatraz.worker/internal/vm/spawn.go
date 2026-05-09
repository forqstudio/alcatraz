package vm

import (
	"context"
	"fmt"
	"log/slog"
	"net"
	"os"
	"path/filepath"
	"time"

	"alcatraz.worker/internal/messaging"
	"alcatraz.worker/internal/vm/agentfs"
	firecracker "github.com/firecracker-microvm/firecracker-go-sdk"
	"github.com/firecracker-microvm/firecracker-go-sdk/client/models"
)

type SpawnOptions struct {
	FirecrackerBin string
	Rootfs         string
	Kernel         string
	AgentfsData    string
	// CAPubkeyPath is read on every spawn and written into the AgentFS overlay
	// at /etc/ssh/trusted_user_ca_keys, so sshd inside the VM accepts certs
	// signed by alcatraz.api's CA. If empty or unreadable, spawn aborts.
	CAPubkeyPath string
	// Publisher fires vm.ready after boot (and the post-exit cleanup goroutine
	// fires vm.destroyed). Optional — if nil, no events are published.
	Publisher *messaging.Publisher
}

const (
	guestSshUser   = "al"
	guestSshPort   = 22
	sshdProbeWait  = 10 * time.Second
	sshdProbeTick  = 200 * time.Millisecond
)

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

	// Plant the auth_principals + trusted_user_ca_keys files into the overlay
	// before the VM boots. sshd reads these at startup; the cert principal is
	// the sandbox UUID and the CA pubkey gates which signers it accepts.
	if err := writeOverlayBootFiles(ctx, instance.agentID, spawnOptions.Rootfs, spawnOptions.AgentfsData, spawnOptions.CAPubkeyPath); err != nil {
		return nil, fmt.Errorf("write overlay boot files: %w", err)
	}
	slog.Info("VM ssh trust files written into overlay", "vm_id", instance.id)

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
			instance.SetVMIP(ipConf.IPAddr.IP.String())
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

	// Wait for sshd inside the VM before announcing readiness. tap+IP exist as
	// soon as Start returns; userspace sshd needs another second or two.
	if vmIP := instance.GetVMIP(); vmIP != "" {
		if err := waitForSshd(ctx, vmIP, guestSshPort, sshdProbeWait); err != nil {
			slog.Warn("sshd probe timed out, announcing vm.ready anyway", "vm_id", instance.id, "err", err)
		}
		if spawnOptions.Publisher != nil {
			if err := spawnOptions.Publisher.PublishVMReady(ctx, instance.id, vmIP, guestSshPort); err != nil {
				slog.Error("publish vm.ready failed", "vm_id", instance.id, "err", err)
			} else {
				slog.Info("Published vm.ready", "vm_id", instance.id, "host", vmIP, "port", guestSshPort)
			}
		}
	}

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

		if spawnOptions.Publisher != nil {
			if err := spawnOptions.Publisher.PublishVMDestroyed(context.Background(), id); err != nil {
				slog.Error("publish vm.destroyed failed", "vm_id", id, "err", err)
			} else {
				slog.Info("Published vm.destroyed", "vm_id", id)
			}
		}
	}()

	success = true
	return instance, nil
}

// writeOverlayBootFiles plants /etc/ssh/auth_principals/al (sandbox UUID) and
// /etc/ssh/trusted_user_ca_keys (alcatraz.api's CA pubkey) into the AgentFS
// overlay so the rootfs's sshd accepts the cert pipeline. It opens the overlay
// just for these writes — OpenAndServe (called later by the StartNFS handler)
// reopens against the same SQLite file and sees the committed writes.
func writeOverlayBootFiles(ctx context.Context, agentID, rootfs, dataDir, caPubkeyPath string) error {
	if caPubkeyPath == "" {
		return fmt.Errorf("CAPubkeyPath is empty; set WORKER_CA_PUBKEY_PATH")
	}
	caPub, err := os.ReadFile(caPubkeyPath)
	if err != nil {
		return fmt.Errorf("read CA pubkey from %s: %w", caPubkeyPath, err)
	}

	handle, err := agentfs.OpenOverlay(ctx, agentID, rootfs, dataDir)
	if err != nil {
		return fmt.Errorf("open overlay for boot writes: %w", err)
	}
	defer handle.Close()

	if err := handle.WriteFile(ctx, "/etc/ssh/auth_principals/al", []byte(agentID+"\n"), 0o644); err != nil {
		return err
	}
	if err := handle.WriteFile(ctx, "/etc/ssh/trusted_user_ca_keys", caPub, 0o644); err != nil {
		return err
	}
	return nil
}

// waitForSshd dials host:port until it accepts or the timeout elapses. Used to
// gate vm.ready on actual sshd availability rather than just Firecracker boot.
func waitForSshd(ctx context.Context, host string, port int, timeout time.Duration) error {
	addr := fmt.Sprintf("%s:%d", host, port)
	deadline := time.Now().Add(timeout)
	dialer := &net.Dialer{Timeout: sshdProbeTick}

	for {
		conn, err := dialer.DialContext(ctx, "tcp", addr)
		if err == nil {
			_ = conn.Close()
			return nil
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("sshd not reachable at %s after %s: %w", addr, timeout, err)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(sshdProbeTick):
		}
	}
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
