package main

import (
	"context"
	"log"
	"os"
	"os/signal"
	"syscall"

	"alcatraz.worker/internal/config"
	"alcatraz.worker/internal/nats"
	"alcatraz.worker/internal/vm"
)

func main() {
	cfg := config.DefaultConfig()

	agentfsBin, err := vm.FindAgentfsBin(cfg.AgentfsBin)
	if err != nil {
		log.Printf("Warning: agentfs binary not found: %v", err)
	} else {
		cfg.AgentfsBin = agentfsBin
	}

	mgr := vm.NewInstanceManager(cfg.MaxVMs)

	handler := func(req *config.VMRequest) error {
		opts := &vm.SpawnOptions{
			AgentfsBin:    	cfg.AgentfsBin,
			FirecrackerBin: cfg.FirecrackerBin,
			Rootfs:        	cfg.Rootfs,
			Kernel:        	cfg.Kernel,
			AgentfsDir:    	cfg.AgentfsDir,
		}

		ctx := context.Background()
		_, err := vm.Spawn(ctx, mgr, req, opts)
		return err
	}

	sub, err := nats.NewSubscriber(cfg.NATSURL, cfg.Subject, cfg.QueueGroup, handler)
	if err != nil {
		log.Fatalf("Failed to create subscriber: %v", err)
	}

	if err := sub.Start(); err != nil {
		log.Fatalf("Failed to start subscriber: %v", err)
	}

	log.Printf("Alcatraz Worker started, connected to %s", sub.URL())

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	<-sigCh

	log.Println("Shutting down...")
	sub.Stop()
}