package vm

import (
	"log"
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
	log.Printf("IPAM sweep: scanning %s for stale leases", cniIPAMDir)
	entries, err := os.ReadDir(cniIPAMDir)
	if err != nil {
		if !os.IsNotExist(err) {
			log.Printf("IPAM sweep: read %s: %v", cniIPAMDir, err)
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
			log.Printf("IPAM sweep: remove %s: %v", path, err)
			continue
		}
		removed++
	}
	if removed > 0 {
		log.Printf("IPAM sweep: removed %d stale entr(ies) from %s", removed, cniIPAMDir)
	} else {
		log.Printf("IPAM sweep: no stale entries")
	}
}
