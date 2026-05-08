package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"os"
	"os/signal"
	"syscall"
	"time"

	"alcatraz.worker/internal/logging"
	"alcatraz.worker/internal/messaging"
	"alcatraz.worker/internal/vm"
)

const defaultCAPubkeyPath = "/run/alcatraz-ca/alcatraz_ca.pub"

func main() {
	shutdownLogs := logging.Init()

	vmConfig := vm.DefaultConfig()

	natsConfig, err := messaging.LoadConfig()
	if err != nil {
		logging.Fatal("Failed to load NATS config", "err", err)
	}

	caPubkeyPath := os.Getenv("WORKER_CA_PUBKEY_PATH")
	if caPubkeyPath == "" {
		caPubkeyPath = defaultCAPubkeyPath
	}

	slog.Info("Worker starting",
		"firecracker", vmConfig.FirecrackerBin,
		"rootfs", vmConfig.Rootfs,
		"kernel", vmConfig.Kernel,
		"agentfs_data", vmConfig.AgentfsData,
		"ca_pubkey", caPubkeyPath,
	)

	vm.SweepIPAM()

	mgr := vm.NewVirtualMachineService(vmConfig)
	slog.Info("VM service ready", "max_vms", mgr.GetMaxVMs())

	publisher, err := messaging.NewPublisher(natsConfig.URL, natsConfig.ReadySubject, natsConfig.DestroyedSubject)
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
		CAPubkeyPath:   caPubkeyPath,
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

	slog.Info("Alcatraz Worker started",
		"nats_url", subscriber.URL(),
		"ready_subject", natsConfig.ReadySubject,
		"destroyed_subject", natsConfig.DestroyedSubject,
	)

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	<-sigCh

	slog.Info("Shutting down")
	if err := subscriber.Stop(); err != nil {
		slog.Error("Subscriber shutdown error", "err", err)
	}

	shutdownCtx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	mgr.Shutdown(shutdownCtx)

	flushCtx, flushCancel := context.WithTimeout(context.Background(), 2*time.Second)
	shutdownLogs(flushCtx)
	flushCancel()
}
