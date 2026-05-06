package main

import (
	"context"
	"log"
	"os"
	"os/signal"
	"syscall"

	messaging "alcatraz.worker/internal/messaging"
	virtualMachine "alcatraz.worker/internal/vm"
)

func main() {
	vmConfig := virtualMachine.GetConfig()

	natsConfig, err := messaging.LoadConfig()
	if err != nil {
		log.Fatalf("Failed to load NATS config: %v", err)
	}

	mgr := virtualMachine.NewVirtualMachineService()

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