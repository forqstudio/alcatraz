package logging

import (
	"context"
	"log/slog"
	"os"
	"sync"
	"time"

	"github.com/joho/godotenv"
)

type Closer func(ctx context.Context)

var (
	mu             sync.Mutex
	globalShutdown Closer
)

// Init configures slog as the default logger, fanning records out to stdout
// (text format) and — when SEQ_URL is set — to Seq via CLEF over HTTP. It
// returns a Closer that flushes any pending Seq events; callers should defer
// it with a bounded context.
func Init() Closer {
	_ = godotenv.Load(".env")

	app := envOr("APPLICATION", "alcatraz-worker")
	environment := envOr("ENVIRONMENT", "development")
	seqURL := os.Getenv("SEQ_URL")
	seqAPIKey := os.Getenv("SEQ_API_KEY")

	level := slog.LevelInfo

	handlers := []slog.Handler{
		slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: level}),
	}

	var seq *seqHandler
	if seqURL != "" {
		seq = newSeqHandler(seqURL, seqAPIKey, level)
		handlers = append(handlers, seq)
	}

	logger := slog.New(newMultiHandler(handlers...)).With(
		"Application", app,
		"Environment", environment,
	)
	slog.SetDefault(logger)

	closer := func(ctx context.Context) {
		if seq != nil {
			seq.Close(ctx)
		}
	}

	mu.Lock()
	globalShutdown = closer
	mu.Unlock()

	return closer
}

// Fatal logs at Error level, flushes any pending Seq events (bounded by 2s),
// then exits the process with code 1. Use as a drop-in for log.Fatalf.
func Fatal(msg string, args ...any) {
	slog.Error(msg, args...)

	mu.Lock()
	shutdown := globalShutdown
	mu.Unlock()

	if shutdown != nil {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		shutdown(ctx)
		cancel()
	}
	os.Exit(1)
}

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}
