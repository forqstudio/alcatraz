package metering

import (
	"os"
	"path/filepath"
	"testing"
)

func writeCpuStat(t *testing.T, content string) string {
	t.Helper()
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "cpu.stat"), []byte(content), 0o644); err != nil {
		t.Fatalf("write cpu.stat: %v", err)
	}
	return dir
}

func TestReadCpuUsageUsec_HappyPath(t *testing.T) {
	dir := writeCpuStat(t, "usage_usec 123456789\nuser_usec 100\nsystem_usec 200\n")
	got, err := ReadCpuUsageUsec(dir)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != 123456789 {
		t.Fatalf("got %d, want 123456789", got)
	}
}

func TestReadCpuUsageUsec_MissingCgroup_ReturnsZero(t *testing.T) {
	got, err := ReadCpuUsageUsec("/nonexistent/cgroup")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != 0 {
		t.Fatalf("got %d, want 0", got)
	}
}

func TestReadCpuUsageUsec_MissingField_ReturnsError(t *testing.T) {
	dir := writeCpuStat(t, "user_usec 100\nsystem_usec 200\n")
	_, err := ReadCpuUsageUsec(dir)
	if err == nil {
		t.Fatalf("expected error when usage_usec absent")
	}
}

func TestReadCpuUsageUsec_MalformedValue_ReturnsError(t *testing.T) {
	dir := writeCpuStat(t, "usage_usec not-a-number\n")
	_, err := ReadCpuUsageUsec(dir)
	if err == nil {
		t.Fatalf("expected error for malformed value")
	}
}
