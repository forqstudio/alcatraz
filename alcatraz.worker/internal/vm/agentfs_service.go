package vm

import (
	"context"
	"log"
	"os"

	afsworker "alcatraz.worker/internal/vm/agentfs"
)

// NFSProcess is the lifecycle handle exposed to the rest of the worker.
// The implementation is now an in-process Go NFS server (see internal/vm/agentfs).
type NFSProcess interface {
	GetProcess() interface{}
	Kill() error
	Wait() error
}

// PrepareAgentfsOverlay materializes the per-agent overlay database, replacing
// `agentfs init --force --base <rootfs> <id>`.
func PrepareAgentfsOverlay(instance VirtualMachineInfo, rootfsPath, dataDir string) error {
	return afsworker.PrepareOverlay(instance.GetAgentID(), rootfsPath, dataDir)
}

// StartAgentfsNFS opens the overlay and starts the in-process NFSv3 server.
func StartAgentfsNFS(ctx context.Context, instance VirtualMachineInfo, rootfsPath, dataDir string) (NFSProcess, error) {
	handle, err := afsworker.OpenOverlay(ctx, instance.GetAgentID(), rootfsPath, dataDir)
	if err != nil {
		return nil, err
	}
	srv, err := afsworker.StartNFS(handle, instance.GetHostTapIP(), instance.GetNFSPort())
	if err != nil {
		_ = handle.Close()
		return nil, err
	}
	return srv, nil
}

// CleanupInstance shuts down the NFS server (if any) and removes the firecracker
// socket. The maxSlots argument is retained for compatibility but unused.
func CleanupInstance(instance VirtualMachineInfo, maxSlots int) {
	log.Printf("Cleaning up instance %s", instance.GetID())

	if proc := instance.GetNFSProcess(); proc != nil {
		_ = proc.Kill()
		_ = proc.Wait()
	}

	if FileExists(instance.GetSocket()) {
		os.Remove(instance.GetSocket())
	}
}
