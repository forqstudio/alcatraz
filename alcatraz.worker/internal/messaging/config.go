package messaging

import (
	"errors"
	"fmt"
	"io/fs"
	"os"

	"github.com/joho/godotenv"
)

const EnvFile = ".env"

const (
	DefaultURL                = "nats://localhost:4222"
	DefaultSubject            = "vm.spawn"
	// Queue groups follow the <consumer>-<subject> convention shared with the
	// API (`api-vm-ready`, `api-vm-destroyed`). One group per subject so each
	// worker subscription competes only with its own peers.
	DefaultQueueGroup         = "worker-vm-spawn"
	DefaultReadySubject       = "vm.ready"
	DefaultDestroyedSubject   = "vm.destroyed"
	DefaultDestroySubject     = "vm.destroy"
	DefaultDestroyQueueGroup  = "worker-vm-destroy"
)

type Config struct {
	URL                string
	Subject            string
	QueueGroup         string
	ReadySubject       string
	DestroyedSubject   string
	DestroySubject     string
	DestroyQueueGroup  string
}

// LoadConfig returns the messaging config. It starts from DefaultConfig() and
// overlays any NATS_* env vars (loaded from .env if present). A missing .env
// is not an error.
func LoadConfig() (*Config, error) {
	if err := godotenv.Load(EnvFile); err != nil && !errors.Is(err, fs.ErrNotExist) {
		return nil, fmt.Errorf("load %s: %w", EnvFile, err)
	}

	cfg := DefaultConfig()
	if v := os.Getenv("NATS_URL"); v != "" {
		cfg.URL = v
	}
	if v := os.Getenv("NATS_SUBJECT"); v != "" {
		cfg.Subject = v
	}
	if v := os.Getenv("NATS_QUEUE_GROUP"); v != "" {
		cfg.QueueGroup = v
	}
	if v := os.Getenv("NATS_READY_SUBJECT"); v != "" {
		cfg.ReadySubject = v
	}
	if v := os.Getenv("NATS_DESTROYED_SUBJECT"); v != "" {
		cfg.DestroyedSubject = v
	}
	if v := os.Getenv("NATS_DESTROY_SUBJECT"); v != "" {
		cfg.DestroySubject = v
	}
	if v := os.Getenv("NATS_DESTROY_QUEUE_GROUP"); v != "" {
		cfg.DestroyQueueGroup = v
	}
	return cfg, nil
}

func DefaultConfig() *Config {
	return &Config{
		URL:               DefaultURL,
		Subject:           DefaultSubject,
		QueueGroup:        DefaultQueueGroup,
		ReadySubject:      DefaultReadySubject,
		DestroyedSubject:  DefaultDestroyedSubject,
		DestroySubject:    DefaultDestroySubject,
		DestroyQueueGroup: DefaultDestroyQueueGroup,
	}
}
