package writer

import (
	"context"
	"log/slog"
	"time"

	"alcatraz.routes/internal/registry"
)

// Debouncer collapses bursts of registry changes into a single file write.
// Pump() drains a "dirty" channel and writes after the debounce window quiets.
type Debouncer struct {
	reg           *registry.Registry
	outputPath    string
	gatewayDomain string
	debounce      time.Duration

	dirty chan struct{}
}

func New(reg *registry.Registry, outputPath, gatewayDomain string, debounce time.Duration) *Debouncer {
	return &Debouncer{
		reg:           reg,
		outputPath:    outputPath,
		gatewayDomain: gatewayDomain,
		debounce:      debounce,
		dirty:         make(chan struct{}, 1),
	}
}

// Notify signals that the registry changed. Coalesces — multiple Notify calls
// before the next write are equivalent to one.
func (d *Debouncer) Notify() {
	select {
	case d.dirty <- struct{}{}:
	default:
	}
}

// Pump runs until ctx is done. On every Notify, waits `debounce` for quiet,
// then writes. Returns ctx.Err on shutdown.
func (d *Debouncer) Pump(ctx context.Context) error {
	// Write an initial empty file so Traefik has something to read on startup.
	if err := d.write(); err != nil {
		slog.Error("routes: initial write failed", "err", err)
	}

	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-d.dirty:
		}

		// Drain any extra notifies during the debounce window.
		timer := time.NewTimer(d.debounce)
		drainLoop:
		for {
			select {
			case <-d.dirty:
				if !timer.Stop() {
					<-timer.C
				}
				timer.Reset(d.debounce)
			case <-timer.C:
				break drainLoop
			case <-ctx.Done():
				timer.Stop()
				return ctx.Err()
			}
		}

		if err := d.write(); err != nil {
			slog.Error("routes: write failed", "err", err, "path", d.outputPath)
		}
	}
}

func (d *Debouncer) write() error {
	entries := d.reg.Snapshot()
	body, err := Render(entries, d.gatewayDomain)
	if err != nil {
		return err
	}
	if err := WriteAtomic(d.outputPath, body); err != nil {
		return err
	}
	slog.Info("routes: wrote dynamic config",
		"path", d.outputPath,
		"route_count", len(entries),
	)
	return nil
}
