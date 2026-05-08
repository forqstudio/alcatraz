package main

import (
	"context"
	"errors"
	"log/slog"
	"os"
	"os/signal"
	"sync"
	"syscall"

	"alcatraz.routes/internal/config"
	"alcatraz.routes/internal/registry"
	"alcatraz.routes/internal/writer"

	"github.com/nats-io/nats.go"
)

func main() {
	slog.SetDefault(slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo})))

	cfg, err := config.Load()
	if err != nil {
		slog.Error("routes: failed to load config", "err", err)
		os.Exit(1)
	}

	slog.Info("routes: starting",
		"nats_url", cfg.NatsURL,
		"output_path", cfg.OutputPath,
		"gateway_domain", cfg.GatewayDomain,
		"debounce_ms", cfg.DebounceMs,
	)

	nc, err := nats.Connect(cfg.NatsURL)
	if err != nil {
		slog.Error("routes: failed to connect to NATS", "err", err)
		os.Exit(1)
	}
	defer nc.Drain()

	reg := registry.New()
	deb := writer.New(reg, cfg.OutputPath, cfg.GatewayDomain, cfg.Debounce())
	consumer := registry.NewNATSConsumer(
		nc, reg, cfg.ReadySubject, cfg.DestroyedSubject, deb.Notify,
	)

	ctx, cancel := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer cancel()

	var wg sync.WaitGroup
	wg.Add(2)
	go func() {
		defer wg.Done()
		if err := consumer.Run(ctx); err != nil && !errors.Is(err, context.Canceled) {
			slog.Error("routes: consumer error", "err", err)
		}
	}()
	go func() {
		defer wg.Done()
		if err := deb.Pump(ctx); err != nil && !errors.Is(err, context.Canceled) {
			slog.Error("routes: pump error", "err", err)
		}
	}()

	wg.Wait()
	slog.Info("routes: shutting down")
}
