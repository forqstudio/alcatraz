package vm

import (
	"context"
	"encoding/base64"
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
	// CAPubkey is the API's SSH CA public key, read once at worker startup.
	// It rides on the kernel cmdline as base64 and is materialised into a
	// tmpfs at /run/ssh-config/trusted_user_ca_keys by the guest's /init.
	CAPubkey []byte
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
	spawnStart := time.Now()

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

	overlayStart := time.Now()
	if err := agentfs.PrepareOverlay(ctx, instance.agentID, spawnOptions.Rootfs, spawnOptions.AgentfsData); err != nil {
		return nil, fmt.Errorf("prepare agentfs: %w", err)
	}
	phaseOverlayPrep := time.Since(overlayStart)
	slog.Info("VM agentfs overlay ready",
		"vm_id", instance.id,
		"data", spawnOptions.AgentfsData,
	)

	// The CNI host-local IPAM gateway is the first IP in SubnetCIDR. We set it
	// before m.Start so the AppendAfter(SetupNetwork) handler below can read
	// instance.hostTapIP when it binds the NFS listener. The post-Start block
	// re-reads the gateway from the CNI result so the value is authoritative.
	instance.SetHostTapIP(GatewayIP)

	// SSH cert config rides on the kernel cmdline: the sandbox UUID is the
	// cert principal sshd must accept (AuthorizedPrincipalsFile) and the CA
	// pubkey gates which signers it trusts (TrustedUserCAKeys). The guest's
	// /init parses these args and writes them into /run/ssh-config (tmpfs)
	// before sshd starts.
	caPubkeyB64 := base64.StdEncoding.EncodeToString(spawnOptions.CAPubkey)
	bootArgs := fmt.Sprintf(
		"console=ttyS0 reboot=k panic=1 pci=off %s root=/dev/nfs nfsroot=%s:/,nfsvers=3,tcp,nolock,port=%d,mountport=%d rw init=/init alcatraz.agent_id=%s alcatraz.ca_pubkey=%s",
		instance.kernelArgs,
		GatewayIP,
		instance.nfsPort,
		instance.nfsPort,
		instance.agentID,
		caPubkeyB64,
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
	fcBootStart := time.Now()
	if err := m.Start(ctx); err != nil {
		return nil, fmt.Errorf("start machine: %w", err)
	}
	phaseFcBoot := time.Since(fcBootStart)

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
		sshdProbeStart := time.Now()
		if err := waitForSshd(ctx, vmIP, guestSshPort, sshdProbeWait); err != nil {
			slog.Warn("sshd probe timed out, announcing vm.ready anyway", "vm_id", instance.id, "err", err)
		}
		phaseSshdProbe := time.Since(sshdProbeStart)

		if spawnOptions.Publisher != nil {
			info := buildVMReadyInfo(ctx, instance, spawnOptions, vmIP, spawnStart, phaseOverlayPrep, phaseFcBoot, phaseSshdProbe)
			if err := spawnOptions.Publisher.PublishVMReady(ctx, info); err != nil {
				slog.Error("publish vm.ready failed", "vm_id", instance.id, "err", err)
			} else {
				slog.Info("Published vm.ready",
					"vm_id", instance.id,
					"host", vmIP,
					"port", guestSshPort,
					"boot_duration_ms", info.BootDurationMs,
				)
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

// buildVMReadyInfo assembles the enriched payload for vm.ready. SDK metadata
// calls (DescribeInstanceInfo, PID) are best-effort: failure leaves the
// corresponding pointer nil but never aborts the publish, since the VM is
// already reachable on SSH at this point.
func buildVMReadyInfo(
	ctx context.Context,
	instance *VirtualMachine,
	spawnOptions *SpawnOptions,
	vmIP string,
	spawnStart time.Time,
	phaseOverlayPrep, phaseFcBoot, phaseSshdProbe time.Duration,
) messaging.VMReadyInfo {
	info := messaging.VMReadyInfo{
		ID:                 instance.id,
		Host:               vmIP,
		Port:               guestSshPort,
		ActualVcpus:        instance.vcpus,
		ActualMemoryMib:    instance.memoryMib,
		BootDurationMs:     time.Since(spawnStart).Milliseconds(),
		ReadyAtUtc:         time.Now().UTC(),
		SocketPath:         instance.socket,
		TapDevice:          instance.tapDev,
		MacAddress:         GuestMAC,
		VmIp:               vmIP,
		HostGatewayIp:      instance.hostTapIP,
		NfsPort:            instance.nfsPort,
		WorkerSlotIndex:    instance.index,
		RootfsPath:         spawnOptions.Rootfs,
		KernelPath:         spawnOptions.Kernel,
		PhaseOverlayPrepMs: phaseOverlayPrep.Milliseconds(),
		PhaseFcBootMs:      phaseFcBoot.Milliseconds(),
		PhaseSshdProbeMs:   phaseSshdProbe.Milliseconds(),
	}

	if m := instance.machine; m != nil {
		if fcInfo, err := m.DescribeInstanceInfo(ctx); err != nil {
			slog.Warn("DescribeInstanceInfo failed; vmm metadata will be nil", "vm_id", instance.id, "err", err)
		} else {
			info.VmmVersion = fcInfo.VmmVersion
			info.VmmState = fcInfo.State
		}
		if pid, err := m.PID(); err != nil {
			slog.Warn("PID lookup failed; firecracker_pid will be nil", "vm_id", instance.id, "err", err)
		} else {
			info.FirecrackerPid = &pid
		}
	}

	return info
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
