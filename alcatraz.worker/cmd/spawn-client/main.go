package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"log/slog"
	"time"

	"github.com/nats-io/nats.go"

	"alcatraz.worker/internal/logging"
	"alcatraz.worker/internal/vm"
)

const (
	defaultURL     = "nats://localhost:4222"
	defaultSubject = "vm.spawn"
)

var (
	natsURL     string
	natsSubject string
	vmID        string
	vcpus       int
	memory      int
	kernelArgs  string
)

func main() {
	shutdownLogs := logging.Init()
	defer func() {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		shutdownLogs(ctx)
		cancel()
	}()

	flag.StringVar(&natsURL, "nats-url", defaultURL, "NATS server URL")
	flag.StringVar(&natsSubject, "subject", defaultSubject, "NATS subject to publish to")
	flag.StringVar(&vmID, "id", "", "VM ID (auto-generated if omitted)")
	flag.IntVar(&vcpus, "vcpus", 0, "vCPU count (default: 4)")
	flag.IntVar(&memory, "mem", 0, "Memory in MiB (default: 8192)")
	flag.StringVar(&kernelArgs, "kernel-args", "", "Kernel boot args")

	flag.Parse()

	if vcpus < 0 {
		vcpus = 0
	}
	if memory < 0 {
		memory = 0
	}

	vmRequest := vm.CreateVirtualMachineInput{
		ID:         vmID,
		VCPUs:      vcpus,
		MemoryMib:  memory,
		KernelArgs: kernelArgs,
	}

	data, err := json.Marshal(vmRequest)
	if err != nil {
		logging.Fatal("Failed to marshal request", "err", err)
	}

	connection, err := nats.Connect(natsURL)
	if err != nil {
		logging.Fatal("Failed to connect to NATS", "err", err, "nats_url", natsURL)
	}
	defer connection.Close()

	if err := connection.Publish(natsSubject, data); err != nil {
		logging.Fatal("Failed to publish", "err", err, "subject", natsSubject)
	}

	if err := connection.Flush(); err != nil {
		logging.Fatal("Failed to flush", "err", err)
	}

	slog.Info("Published spawn request",
		"subject", natsSubject,
		"id", vmRequest.ID,
		"vcpus", vmRequest.VCPUs,
		"memory_mib", vmRequest.MemoryMib,
	)
	fmt.Printf("Published spawn request to %s: %s\n", natsSubject, string(data))
}
