package vm

import (
	"context"

	afsworker "alcatraz.worker/internal/vm/agentfs"
)

// NFSProcess is the lifecycle handle exposed to the rest of the worker.
// The implementation is now an in-process Go NFS server (see internal/vm/agentfs).
type NFSProcess interface {
	Kill() error
	Wait() error
}

// PrepareAgentfsOverlay materializes the per-agent overlay database, replacing
// `agentfs init --force --base <rootfs> <id>`.
func PrepareAgentfsOverlay(ctx context.Context, instance VirtualMachineInfo, rootfsPath, dataDir string) error {
	return afsworker.PrepareOverlay(ctx, instance.GetAgentID(), rootfsPath, dataDir)
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
