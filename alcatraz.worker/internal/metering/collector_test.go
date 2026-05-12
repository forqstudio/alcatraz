package metering

import (
	"context"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"
)

type stubPublisher struct {
	mu      sync.Mutex
	samples []SamplePayload
	final   *FinalPayload
}

func (s *stubPublisher) PublishUsageSample(_ context.Context, p SamplePayload) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.samples = append(s.samples, p)
	return nil
}

func (s *stubPublisher) PublishUsageFinal(_ context.Context, p FinalPayload) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	p2 := p
	s.final = &p2
	return nil
}

func (s *stubPublisher) sampleCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.samples)
}

func setupFiles(t *testing.T, cpuUsec int64, rx, tx int64) (cgroupPath, metricsPath string) {
	t.Helper()
	cgroupPath = t.TempDir()
	if err := os.WriteFile(
		filepath.Join(cgroupPath, "cpu.stat"),
		[]byte("usage_usec "+itoa(cpuUsec)+"\n"),
		0o644,
	); err != nil {
		t.Fatalf("write cpu.stat: %v", err)
	}

	metricsDir := t.TempDir()
	metricsPath = filepath.Join(metricsDir, "metrics.fifo")
	body := `{"net":{"rx_bytes_count":` + itoa(rx) + `,"tx_bytes_count":` + itoa(tx) + `}}` + "\n"
	if err := os.WriteFile(metricsPath, []byte(body), 0o644); err != nil {
		t.Fatalf("write metrics: %v", err)
	}
	return cgroupPath, metricsPath
}

func itoa(n int64) string {
	if n == 0 {
		return "0"
	}
	neg := false
	if n < 0 {
		neg = true
		n = -n
	}
	buf := [20]byte{}
	i := len(buf)
	for n > 0 {
		i--
		buf[i] = byte('0' + n%10)
		n /= 10
	}
	if neg {
		i--
		buf[i] = '-'
	}
	return string(buf[i:])
}

func TestCollector_PublishesSamplesAndFinal(t *testing.T) {
	cgroup, metrics := setupFiles(t, 42_000_000, 1_000_000, 2_000_000)
	pub := &stubPublisher{}

	c := Start(context.Background(), Options{
		SandboxID:   "sandbox-1",
		CgroupPath:  cgroup,
		MetricsPath: metrics,
		BootedAt:    time.Now().Add(-2 * time.Second),
		Interval:    25 * time.Millisecond,
		Publisher:   pub,
	})

	// Allow a few ticks.
	deadline := time.Now().Add(500 * time.Millisecond)
	for pub.sampleCount() < 2 && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}

	c.Stop(context.Background())

	pub.mu.Lock()
	defer pub.mu.Unlock()
	if len(pub.samples) < 2 {
		t.Fatalf("expected ≥2 samples, got %d", len(pub.samples))
	}
	last := pub.samples[len(pub.samples)-1]
	if last.CpuUsageUsecCumulative == nil || *last.CpuUsageUsecCumulative != 42_000_000 {
		t.Fatalf("unexpected sample cpu: %+v", last.CpuUsageUsecCumulative)
	}
	if last.NetRxBytesCumulative == nil || *last.NetRxBytesCumulative != 1_000_000 {
		t.Fatalf("unexpected sample rx: %+v", last.NetRxBytesCumulative)
	}

	if pub.final == nil {
		t.Fatalf("expected final payload")
	}
	if pub.final.SandboxID != "sandbox-1" {
		t.Fatalf("unexpected sandbox_id: %s", pub.final.SandboxID)
	}
	if pub.final.TotalCpuUsageUsec == nil || *pub.final.TotalCpuUsageUsec != 42_000_000 {
		t.Fatalf("unexpected final cpu: %+v", pub.final.TotalCpuUsageUsec)
	}
	if pub.final.SampleCount < 2 {
		t.Fatalf("expected sample_count ≥2, got %d", pub.final.SampleCount)
	}
}

func TestCollector_NoSourcesConfigured_StillEmitsFinal(t *testing.T) {
	pub := &stubPublisher{}
	c := Start(context.Background(), Options{
		SandboxID: "sandbox-empty",
		BootedAt:  time.Now(),
		Interval:  25 * time.Millisecond,
		Publisher: pub,
	})
	time.Sleep(80 * time.Millisecond)
	c.Stop(context.Background())

	pub.mu.Lock()
	defer pub.mu.Unlock()
	if pub.final == nil {
		t.Fatalf("expected final payload even without sources")
	}
	if pub.final.TotalCpuUsageUsec != nil {
		t.Fatalf("expected nil CPU, got %+v", pub.final.TotalCpuUsageUsec)
	}
}
