package main

import (
	"context"
	"log/slog"
	"os"
	"os/signal"
	"syscall"
	"time"

	"alcatraz.worker/internal/logging"
	messaging "alcatraz.worker/internal/messaging"
	virtualMachine "alcatraz.worker/internal/vm"
)

func main() {
	shutdownLogs := logging.Init()

	vmConfig := virtualMachine.GetConfig()

	natsConfig, err := messaging.LoadConfig()
	if err != nil {
		logging.Fatal("Failed to load NATS config", "err", err)
	}

	slog.Info("Worker starting",
		"firecracker", vmConfig.FirecrackerBin,
		"rootfs", vmConfig.Rootfs,
		"kernel", vmConfig.Kernel,
		"agentfs_data", vmConfig.AgentfsData,
	)

	virtualMachine.SweepIPAM()

	mgr := virtualMachine.NewVirtualMachineService()
	slog.Info("VM service ready", "max_vms", mgr.GetMaxVMs())

	handler := func(message *messaging.Message) error {
		vmRequest := message.ToCreateVirtualMachineInput()
		options := &virtualMachine.SpawnOptions{
			FirecrackerBin: vmConfig.FirecrackerBin,
			Rootfs:         vmConfig.Rootfs,
			Kernel:         vmConfig.Kernel,
			AgentfsData:    vmConfig.AgentfsData,
		}

		ctx := context.Background()
		_, err := virtualMachine.Spawn(ctx, mgr, vmRequest, options)
		return err
	}

	subscriber, err := messaging.NewSubscriber(natsConfig.URL, natsConfig.Subject, natsConfig.QueueGroup, handler)
	if err != nil {
		logging.Fatal("Failed to create subscriber", "err", err)
	}

	if err := subscriber.Start(); err != nil {
		logging.Fatal("Failed to start subscriber", "err", err)
	}

	slog.Info("Alcatraz Worker started", "nats_url", subscriber.URL())

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	<-sigCh

	slog.Info("Shutting down")
	subscriber.Stop()

	shutdownCtx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	mgr.Shutdown(shutdownCtx)

	flushCtx, flushCancel := context.WithTimeout(context.Background(), 2*time.Second)
	shutdownLogs(flushCtx)
	flushCancel()
}
