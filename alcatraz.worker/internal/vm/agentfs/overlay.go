package agentfs

import (
	"context"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"

	sdk "github.com/tursodatabase/agentfs/sdk/go"
)

// OverlayHandle owns the AgentFS database connection and the OverlayFS
// stitched together over the host base layer.
type OverlayHandle struct {
	agentFS *sdk.AgentFS
	base    *HostBase
	overlay *sdk.OverlayFS
}

// OpenOverlay opens (or creates) the database at <dataDir>/<agentID>.db,
// builds the host-backed base layer, and returns the assembled overlay.
func OpenOverlay(ctx context.Context, agentID, rootfsPath, dataDir string) (*OverlayHandle, error) {
	if err := os.MkdirAll(dataDir, 0o755); err != nil {
		return nil, fmt.Errorf("mkdir data dir: %w", err)
	}

	dbPath := filepath.Join(dataDir, agentID+".db")
	afs, err := sdk.Open(ctx, sdk.AgentFSOptions{Path: dbPath})
	if err != nil {
		return nil, fmt.Errorf("open agentfs %s: %w", dbPath, err)
	}

	base, err := NewHostBase(rootfsPath)
	if err != nil {
		afs.Close()
		return nil, fmt.Errorf("open host base: %w", err)
	}

	overlay := sdk.NewOverlayFS(base, afs.FS, afs.DB())
	if err := overlay.Init(ctx); err != nil {
		afs.Close()
		return nil, fmt.Errorf("init overlay: %w", err)
	}
	return &OverlayHandle{agentFS: afs, base: base, overlay: overlay}, nil
}

// Close releases the underlying database connection.
func (h *OverlayHandle) Close() error {
	if h == nil || h.agentFS == nil {
		return nil
	}
	return h.agentFS.Close()
}

// PrepareOverlay replaces `agentfs init --force --base <rootfs> <id>`.
//
// Behaviour:
//   - mkdir dataDir
//   - compute current rootfs stamp
//   - if previous stamp differs OR DB missing, delete <id>.db / -wal / -shm / .base-stamp
//     so the next OpenOverlay rebuilds from scratch
//   - open + close the overlay (to materialise schema)
//   - persist the new stamp
func PrepareOverlay(ctx context.Context, agentID, rootfsPath, dataDir string) error {
	if err := os.MkdirAll(dataDir, 0o755); err != nil {
		return fmt.Errorf("mkdir data dir: %w", err)
	}

	dbPath := filepath.Join(dataDir, agentID+".db")
	stampPath := filepath.Join(dataDir, agentID+".base-stamp")

	currentStamp, err := RootfsStamp(rootfsPath)
	if err != nil {
		return fmt.Errorf("hash rootfs stamp: %w", err)
	}

	needsInit := !fileExists(dbPath)

	if !needsInit && currentStamp != "" && fileExists(stampPath) {
		existing, err := os.ReadFile(stampPath)
		if err == nil && string(existing) != currentStamp {
			slog.Info("Rootfs changed, reinitializing AgentFS overlay", "agent_id", agentID)
			os.Remove(dbPath)
			os.Remove(dbPath + "-wal")
			os.Remove(dbPath + "-shm")
			os.Remove(stampPath)
			needsInit = true
		}
	}

	if needsInit {
		slog.Info("Initializing AgentFS overlay", "agent_id", agentID)
		handle, err := OpenOverlay(ctx, agentID, rootfsPath, dataDir)
		if err != nil {
			return err
		}
		_ = handle.Close()
	} else {
		slog.Info("Reusing existing AgentFS overlay (rootfs unchanged)", "agent_id", agentID)
	}

	if currentStamp != "" {
		if err := os.WriteFile(stampPath, []byte(currentStamp), 0o644); err != nil {
			return fmt.Errorf("write stamp: %w", err)
		}
	}
	return nil
}

func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}
