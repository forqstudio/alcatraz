package vm

import (
	"log/slog"
	"os"
	"path/filepath"
)

const cniIPAMDir = "/var/lib/cni/networks/alcatraz-bridge"

// SweepIPAM clears stale host-local IPAM state for our CNI network. Called at
// worker startup, when no VMs we own are alive yet, so every entry is from a
// previous run. Removing last_reserved_ip.0 also resets the sequential
// allocator so the next spawn gets .10 instead of continuing past wherever
// the previous run stopped.
func SweepIPAM() {
	slog.Info("IPAM sweep: scanning for stale leases", "dir", cniIPAMDir)
	entries, err := os.ReadDir(cniIPAMDir)
	if err != nil {
		if !os.IsNotExist(err) {
			slog.Error("IPAM sweep: read dir failed", "dir", cniIPAMDir, "err", err)
		}
		return
	}
	var removed int
	for _, e := range entries {
		if e.Name() == "lock" {
			continue
		}
		path := filepath.Join(cniIPAMDir, e.Name())
		if err := os.Remove(path); err != nil {
			slog.Error("IPAM sweep: remove failed", "path", path, "err", err)
			continue
		}
		removed++
	}
	if removed > 0 {
		slog.Info("IPAM sweep: removed stale entries", "removed", removed, "dir", cniIPAMDir)
	} else {
		slog.Info("IPAM sweep: no stale entries")
	}
}
