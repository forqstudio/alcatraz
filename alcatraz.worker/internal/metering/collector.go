package metering

import (
	"context"
	"log/slog"
	"sync"
	"time"
)

// SamplePayload is the wire payload for a single tick.
// All counters are cumulative since VM boot; nil values mean the source
// was unreadable for that dimension.
type SamplePayload struct {
	SandboxID              string    `json:"sandbox_id"`
	SampledAtUtc           time.Time `json:"sampled_at_utc"`
	CpuUsageUsecCumulative *int64    `json:"cpu_usage_usec_cumulative,omitempty"`
	NetRxBytesCumulative   *int64    `json:"net_rx_bytes_cumulative,omitempty"`
	NetTxBytesCumulative   *int64    `json:"net_tx_bytes_cumulative,omitempty"`
}

// FinalPayload is the wire payload published on VM exit. Carries the
// last-known cumulative totals plus the sample count for cross-checking
// against the API's persisted samples.
type FinalPayload struct {
	SandboxID         string    `json:"sandbox_id"`
	VmBootedAtUtc     time.Time `json:"vm_booted_at_utc"`
	FinalisedAtUtc    time.Time `json:"finalised_at_utc"`
	TotalCpuUsageUsec *int64    `json:"total_cpu_usage_usec,omitempty"`
	TotalNetRxBytes   *int64    `json:"total_net_rx_bytes,omitempty"`
	TotalNetTxBytes   *int64    `json:"total_net_tx_bytes,omitempty"`
	SampleCount       int       `json:"sample_count"`
}

// Publisher abstracts the JetStream client so the collector can be
// unit-tested against an in-process stub.
type Publisher interface {
	PublishUsageSample(ctx context.Context, payload SamplePayload) error
	PublishUsageFinal(ctx context.Context, payload FinalPayload) error
}

// Options configures a Collector.
type Options struct {
	SandboxID   string
	CgroupPath  string // absolute path, or "" to skip CPU metering
	MetricsPath string // absolute path to Firecracker --metrics-path, or "" to skip net metering
	BootedAt    time.Time
	Interval    time.Duration
	Publisher   Publisher
}

// Collector samples cgroup CPU and Firecracker network counters on a
// fixed interval, publishes a SamplePayload per tick, and publishes a
// FinalPayload on Stop().
type Collector struct {
	opts        Options
	cancel      context.CancelFunc
	done        chan struct{}
	mu          sync.Mutex
	lastSample  SamplePayload
	sampleCount int
}

// Start launches the collector goroutine and returns immediately.
func Start(ctx context.Context, opts Options) *Collector {
	if opts.Interval <= 0 {
		opts.Interval = 60 * time.Second
	}
	cctx, cancel := context.WithCancel(ctx)
	c := &Collector{
		opts:   opts,
		cancel: cancel,
		done:   make(chan struct{}),
	}
	go c.run(cctx)
	return c
}

func (c *Collector) run(ctx context.Context) {
	defer close(c.done)

	ticker := time.NewTicker(c.opts.Interval)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			c.tick(ctx)
		}
	}
}

func (c *Collector) tick(ctx context.Context) {
	sample := c.sampleNow()

	c.mu.Lock()
	c.lastSample = sample
	c.sampleCount++
	c.mu.Unlock()

	if c.opts.Publisher == nil {
		return
	}
	if err := c.opts.Publisher.PublishUsageSample(ctx, sample); err != nil {
		slog.Warn("publish vm.usage_sample failed",
			"sandbox_id", c.opts.SandboxID,
			"err", err)
	}
}

func (c *Collector) sampleNow() SamplePayload {
	now := time.Now().UTC()
	sample := SamplePayload{
		SandboxID:    c.opts.SandboxID,
		SampledAtUtc: now,
	}

	if c.opts.CgroupPath != "" {
		usec, err := ReadCpuUsageUsec(c.opts.CgroupPath)
		if err != nil {
			slog.Warn("read cgroup cpu.stat failed",
				"sandbox_id", c.opts.SandboxID,
				"cgroup_path", c.opts.CgroupPath,
				"err", err)
		} else if usec > 0 {
			sample.CpuUsageUsecCumulative = &usec
		}
	}

	if c.opts.MetricsPath != "" {
		m, err := ReadLastMetrics(c.opts.MetricsPath)
		if err != nil {
			slog.Warn("read fc metrics failed",
				"sandbox_id", c.opts.SandboxID,
				"metrics_path", c.opts.MetricsPath,
				"err", err)
		} else if m != nil {
			rx := m.NetRxBytes
			tx := m.NetTxBytes
			sample.NetRxBytesCumulative = &rx
			sample.NetTxBytesCumulative = &tx
		}
	}

	return sample
}

// Stop cancels the ticker, takes one final sample, and publishes the
// final totals. Blocks until the goroutine has exited and the final
// publish call has returned (so callers can rely on at-least-once
// durability before announcing vm.destroyed).
func (c *Collector) Stop(ctx context.Context) {
	c.cancel()
	<-c.done

	final := c.sampleNow()

	c.mu.Lock()
	// Prefer the freshest snapshot; fall back to the last published sample
	// if the final read returned nothing (cgroup gone, file truncated).
	if final.CpuUsageUsecCumulative == nil {
		final.CpuUsageUsecCumulative = c.lastSample.CpuUsageUsecCumulative
	}
	if final.NetRxBytesCumulative == nil {
		final.NetRxBytesCumulative = c.lastSample.NetRxBytesCumulative
	}
	if final.NetTxBytesCumulative == nil {
		final.NetTxBytesCumulative = c.lastSample.NetTxBytesCumulative
	}
	sampleCount := c.sampleCount
	c.mu.Unlock()

	payload := FinalPayload{
		SandboxID:         c.opts.SandboxID,
		VmBootedAtUtc:     c.opts.BootedAt.UTC(),
		FinalisedAtUtc:    final.SampledAtUtc,
		TotalCpuUsageUsec: final.CpuUsageUsecCumulative,
		TotalNetRxBytes:   final.NetRxBytesCumulative,
		TotalNetTxBytes:   final.NetTxBytesCumulative,
		SampleCount:       sampleCount,
	}

	if c.opts.Publisher == nil {
		return
	}
	if err := c.opts.Publisher.PublishUsageFinal(ctx, payload); err != nil {
		slog.Error("publish vm.usage_final failed",
			"sandbox_id", c.opts.SandboxID,
			"err", err)
	} else {
		slog.Info("Published vm.usage_final",
			"sandbox_id", c.opts.SandboxID,
			"sample_count", sampleCount,
			"total_cpu_usec", derefInt64(payload.TotalCpuUsageUsec),
			"total_rx_bytes", derefInt64(payload.TotalNetRxBytes),
			"total_tx_bytes", derefInt64(payload.TotalNetTxBytes))
	}
}

func derefInt64(p *int64) int64 {
	if p == nil {
		return -1
	}
	return *p
}
