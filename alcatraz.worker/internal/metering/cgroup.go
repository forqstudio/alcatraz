package metering

import (
	"bufio"
	"errors"
	"fmt"
	"os"
	"strconv"
	"strings"
)

// ReadCpuUsageUsec returns the cumulative CPU time consumed by all processes
// in the given cgroup v2, in microseconds. The cgroup path must be an
// absolute filesystem path (e.g. /sys/fs/cgroup/alcatraz.slice/alcatraz-vm-<id>.scope).
//
// Returns (0, nil) if the cgroup does not yet exist — callers should treat
// this as "not yet measurable" rather than an error, because the systemd
// scope may not have materialised the cgroup the instant after Start.
func ReadCpuUsageUsec(cgroupPath string) (int64, error) {
	statPath := cgroupPath + "/cpu.stat"
	f, err := os.Open(statPath)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return 0, nil
		}
		return 0, fmt.Errorf("open %s: %w", statPath, err)
	}
	defer f.Close()

	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		line := scanner.Text()
		if !strings.HasPrefix(line, "usage_usec ") {
			continue
		}
		val, err := strconv.ParseInt(strings.TrimSpace(strings.TrimPrefix(line, "usage_usec")), 10, 64)
		if err != nil {
			return 0, fmt.Errorf("parse usage_usec: %w", err)
		}
		return val, nil
	}
	if err := scanner.Err(); err != nil {
		return 0, fmt.Errorf("scan %s: %w", statPath, err)
	}
	return 0, fmt.Errorf("usage_usec not found in %s", statPath)
}
