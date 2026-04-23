package main

import (
	"context"
	"log"
	"os"
	"os/signal"
	"syscall"

	nats "alcatraz.worker/internal/messaging"
	"alcatraz.worker/internal/vm"
)

func main() {
	vmCfg, err := vm.LoadConfig()
	if err != nil {
		log.Fatalf("Failed to load VM config: %v", err)
	}

	natsCfg, err := nats.LoadConfig()
	if err != nil {
		log.Fatalf("Failed to load NATS config: %v", err)
	}

	mgr := vm.NewInstanceManager(vmCfg.MaxVMs)

	handler := func(msg *nats.Message) error {
		req := msg.ToVMRequest()
		opts := &vm.SpawnOptions{
			AgentfsBin:     vmCfg.AgentfsBin,
			FirecrackerBin: vmCfg.FirecrackerBin,
			Rootfs:         vmCfg.Rootfs,
			Kernel:         vmCfg.Kernel,
			AgentfsData:    vmCfg.AgentfsData,
		}

		ctx := context.Background()
		_, err := vm.Spawn(ctx, mgr, req, opts)
		return err
	}

	subscriber, err := nats.NewSubscriber(natsCfg.URL, natsCfg.Subject, natsCfg.QueueGroup, handler)
	if err != nil {
		log.Fatalf("Failed to create subscriber: %v", err)
	}

	if err := subscriber.Start(); err != nil {
		log.Fatalf("Failed to start subscriber: %v", err)
	}

	log.Printf("Alcatraz Worker started, connected to %s", subscriber.URL())

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	<-sigCh

	log.Println("Shutting down...")
	subscriber.Stop()
}
