package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	"github.com/joho/godotenv"

	"alcatraz.worker/internal/logging"
	"alcatraz.worker/internal/messaging"
	"alcatraz.worker/internal/vm"
)

const defaultCAPubkeyPath = "/run/alcatraz-ca/alcatraz_ca.pub"

func main() {
	// Load .env from a path anchored to the executable, not CWD. Without this,
	// running the binary from anywhere other than alcatraz.worker/ would skip
	// the .env entirely and silently use code defaults — exactly the trap
	// hardcoded relative paths used to set.
	loadDotenvNearExecutable()

	shutdownLogs := logging.Init()

	vmConfig := vm.LoadConfig()
	if err := vmConfig.ValidateArtifacts(); err != nil {
		logging.Fatal("VM artifact validation failed at startup", "err", err)
	}

	natsConfig, err := messaging.LoadConfig()
	if err != nil {
		logging.Fatal("Failed to load NATS config", "err", err)
	}

	caPubkeyPath := os.Getenv("WORKER_CA_PUBKEY_PATH")
	if caPubkeyPath == "" {
		caPubkeyPath = defaultCAPubkeyPath
	}

	// Fail fast at startup rather than on first spawn. /run is tmpfs, so the
	// pubkey disappears across host reboots; without this check, sandboxes
	// silently get stuck in Provisioning. The bytes are read once here and
	// threaded through SpawnOptions — every spawn embeds them (base64'd) in
	// the kernel cmdline, so we never re-read the file per VM.
	caPubkey, err := os.ReadFile(caPubkeyPath)
	if err != nil {
		logging.Fatal(
			"CA pubkey not readable — run alcatraz.worker/scripts/sync-ca-pubkey.sh "+
				"(/run is tmpfs and is wiped on host reboot)",
			"path", caPubkeyPath,
			"err", err,
		)
	}

	slog.Info("Worker starting",
		"firecracker", vmConfig.FirecrackerBin,
		"rootfs", vmConfig.Rootfs,
		"kernel", vmConfig.Kernel,
		"agentfs_data", vmConfig.AgentfsData,
		"ca_pubkey", caPubkeyPath,
	)

	vm.SweepIPAM()
	vm.SweepFailedScopes()

	mgr := vm.NewVirtualMachineService(vmConfig)
	slog.Info("VM service ready", "max_vms", mgr.GetMaxVMs())

	publisher, err := messaging.NewPublisher(
		natsConfig.URL,
		natsConfig.ReadySubject,
		natsConfig.DestroyedSubject,
		natsConfig.UsageSampleSubject,
		natsConfig.UsageFinalSubject,
	)
	if err != nil {
		logging.Fatal("Failed to create publisher", "err", err)
	}
	defer func() {
		if err := publisher.Close(); err != nil {
			slog.Error("publisher close error", "err", err)
		}
	}()

	options := &vm.SpawnOptions{
		FirecrackerBin: vmConfig.FirecrackerBin,
		Rootfs:         vmConfig.Rootfs,
		Kernel:         vmConfig.Kernel,
		AgentfsData:    vmConfig.AgentfsData,
		CAPubkey:       caPubkey,
		Publisher:      publisher,
	}

	handler := func(data []byte) error {
		var req vm.CreateVirtualMachineInput
		if err := json.Unmarshal(data, &req); err != nil {
			return fmt.Errorf("parse spawn request: %w", err)
		}
		slog.Info("Received spawn request",
			"id", req.ID,
			"vcpus", req.VCPUs,
			"memory_mib", req.MemoryMib,
		)
		_, err := vm.Spawn(context.Background(), mgr, &req, options)
		return err
	}

	subscriber, err := messaging.NewSubscriber(natsConfig.URL, natsConfig.Subject, natsConfig.QueueGroup, handler)
	if err != nil {
		logging.Fatal("Failed to create subscriber", "err", err)
	}

	if err := subscriber.Start(); err != nil {
		logging.Fatal("Failed to start subscriber", "err", err)
	}

	// Second subscription: the API publishes vm.destroy when a sandbox is
	// deleted; we StopVMM the matching firecracker process. The post-exit
	// cleanup goroutine in Spawn then publishes vm.destroyed.
	destroyHandler := func(data []byte) error {
		var req struct {
			ID string `json:"id"`
		}
		if err := json.Unmarshal(data, &req); err != nil {
			return fmt.Errorf("parse destroy request: %w", err)
		}
		if req.ID == "" {
			return fmt.Errorf("destroy request missing id")
		}
		slog.Info("Received destroy request", "id", req.ID)
		return mgr.Destroy(req.ID)
	}

	destroySubscriber, err := messaging.NewSubscriber(natsConfig.URL, natsConfig.DestroySubject, natsConfig.DestroyQueueGroup, destroyHandler)
	if err != nil {
		logging.Fatal("Failed to create destroy subscriber", "err", err)
	}
	if err := destroySubscriber.Start(); err != nil {
		logging.Fatal("Failed to start destroy subscriber", "err", err)
	}

	slog.Info("Alcatraz Worker started",
		"nats_url", subscriber.URL(),
		"ready_subject", natsConfig.ReadySubject,
		"destroyed_subject", natsConfig.DestroyedSubject,
		"destroy_subject", natsConfig.DestroySubject,
	)

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	<-sigCh

	slog.Info("Shutting down")
	if err := subscriber.Stop(); err != nil {
		slog.Error("Subscriber shutdown error", "err", err)
	}
	if err := destroySubscriber.Stop(); err != nil {
		slog.Error("Destroy subscriber shutdown error", "err", err)
	}

	shutdownCtx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	mgr.Shutdown(shutdownCtx)

	flushCtx, flushCancel := context.WithTimeout(context.Background(), 2*time.Second)
	shutdownLogs(flushCtx)
	flushCancel()
}

// loadDotenvNearExecutable looks for .env next to alcatraz.worker/, derived
// from the binary's path. Best-effort: silently no-ops if the file is absent
// or os.Executable fails (subsequent CWD-relative load in messaging.LoadConfig
// is the safety net). godotenv.Load does not overwrite existing env vars, so a
// later CWD-relative load is idempotent.
func loadDotenvNearExecutable() {
	exe, err := os.Executable()
	if err != nil {
		return
	}
	if resolved, err := filepath.EvalSymlinks(exe); err == nil {
		exe = resolved
	}
	candidate := filepath.Join(filepath.Dir(filepath.Dir(exe)), ".env")
	_ = godotenv.Load(candidate)
}
