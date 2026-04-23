package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"log"

	"github.com/nats-io/nats.go"
)

const (
	defaultNATSURL = "nats://localhost:4222"
	defaultSubject = "vm.spawn"
)

var (
	natsURL string
	subject string
	vmID    string
	vcpus   int
	mem     int
	args    string
)

type VMRequest struct {
	ID         string `json:"id,omitempty"`
	VCPUs      int    `json:"vcpus,omitempty"`
	MemoryMib  int    `json:"memory_mib,omitempty"`
	KernelArgs string `json:"kernel_args,omitempty"`
}

func main() {
	flag.StringVar(&natsURL, "nats-url", defaultNATSURL, "NATS server URL")
	flag.StringVar(&subject, "subject", defaultSubject, "NATS subject to publish to")
	flag.StringVar(&vmID, "id", "", "VM ID (auto-generated if omitted)")
	flag.IntVar(&vcpus, "vcpus", 0, "vCPU count (default: 4)")
	flag.IntVar(&mem, "mem", 0, "Memory in MiB (default: 8192)")
	flag.StringVar(&args, "kernel-args", "", "Kernel boot args")

	flag.Parse()

	if vcpus < 0 {
		vcpus = 0
	}
	if mem < 0 {
		mem = 0
	}

	req := VMRequest{
		ID:         vmID,
		VCPUs:      vcpus,
		MemoryMib:  mem,
		KernelArgs: args,
	}

	data, err := json.Marshal(req)
	if err != nil {
		log.Fatalf("Failed to marshal request: %v", err)
	}

	nc, err := nats.Connect(natsURL)
	if err != nil {
		log.Fatalf("Failed to connect to NATS: %v", err)
	}
	defer nc.Close()

	if err := nc.Publish(subject, data); err != nil {
		log.Fatalf("Failed to publish: %v", err)
	}

	if err := nc.Flush(); err != nil {
		log.Fatalf("Failed to flush: %v", err)
	}

	fmt.Printf("Published spawn request to %s: %s\n", subject, string(data))
}
