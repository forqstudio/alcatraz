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
	DefaultURL        = "nats://localhost:4222"
	DefaultSubject    = "vm.spawn"
	DefaultQueueGroup = "vm-workers"
)

type Config struct {
	URL        string
	Subject    string
	QueueGroup string
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
	return cfg, nil
}

func DefaultConfig() *Config {
	return &Config{
		URL:        DefaultURL,
		Subject:    DefaultSubject,
		QueueGroup: DefaultQueueGroup,
	}
}
