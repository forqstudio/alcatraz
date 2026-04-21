package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"path/filepath"
	"runtime"
	"syscall"
	"time"

	"firecracker-agentfs/internal/config"
	"firecracker-agentfs/internal/nats"
	"firecracker-agentfs/internal/vm"
)

var (
	natsURL      string
	subject     string
	maxVMs      int
	queueGroup string

	agentfsBin string
	fcBin     string
	rootfs   string
	kernel   string

	agentfsDirAbs string
)

func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func main() {
	flag.StringVar(&natsURL, "nats-url", config.DefaultNATSURL, "NATS server URL")
	flag.StringVar(&subject, "subject", config.DefaultSubject, "NATS subject to subscribe")
	flag.IntVar(&maxVMs, "max-vms", config.DefaultMaxVMs, "Maximum concurrent VMs")
	flag.StringVar(&queueGroup, "queue-group", config.DefaultQueueGroup, "NATS queue group")

	flag.StringVar(&agentfsBin, "agentfs-bin", "", "Path to agentfs binary")
	flag.StringVar(&fcBin, "firecracker-bin", "", "Path to firecracker binary")
	flag.StringVar(&rootfs, "rootfs", config.RootfsPath, "Root filesystem path")
	flag.StringVar(&kernel, "kernel", config.KernelPath, "Kernel path")

	flag.Parse()

	if os.Getuid() != 0 {
		log.Fatal("This program must be run as root")
	}

	if runtime.GOOS != "linux" {
		log.Fatal("This program must be run on Linux")
	}

	if !fileExists(kernel) {
		log.Fatalf("Kernel not found at %s", kernel)
	}
	if !fileExists(rootfs) {
		log.Fatalf("Rootfs not found at %s", rootfs)
	}

	absDir, err := filepath.Abs(config.AgentfsDir)
	if err != nil {
		log.Fatalf("Failed to get absolute path for agentfs dir: %v", err)
	}
	agentfsDirAbs = absDir
	os.MkdirAll(agentfsDirAbs, 0755)

	agentfsPath, err := vm.FindAgentfsBin(agentfsBin)
	if err != nil {
		log.Fatal(err)
	}
	agentfsBin = agentfsPath
	log.Printf("Using agentfs: %s", agentfsBin)

	ctx := context.Background()

	mgr := vm.NewInstanceManager(maxVMs)

	sub, err := nats.NewSubscriber(natsURL, subject, queueGroup, func(req *config.VMRequest) error {
		opts := &vm.SpawnOptions{
			AgentfsBin:     agentfsBin,
			FirecrackerBin: fcBin,
			Rootfs:         rootfs,
			Kernel:         kernel,
			AgentfsDir:     agentfsDirAbs,
		}

		inst, err := vm.Spawn(ctx, mgr, req, opts)
		if err != nil {
			return fmt.Errorf("failed to spawn VM: %w", err)
		}

		log.Printf("Spawned VM %s", inst.ID)
		return nil
	})
	if err != nil {
		log.Fatalf("Failed to create NATS subscriber: %v", err)
	}

	if err := sub.Start(); err != nil {
		log.Fatalf("Failed to start NATS subscriber: %v", err)
	}
	defer sub.Stop()

	log.Printf("Connected to NATS at %s", sub.URL())

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)

	log.Printf("Worker ready (max VMs: %d)", maxVMs)

	for {
		select {
		case sig := <-sigCh:
			log.Printf("Received signal %v, shutting down...", sig)
			for _, inst := range mgr.ListInstances() {
				if inst.Machine != nil {
					log.Printf("Stopping VM %s", inst.ID)
					inst.Machine.StopVMM()
				}
			}
			time.Sleep(2 * time.Second)
			os.Exit(0)
		}
	}
}