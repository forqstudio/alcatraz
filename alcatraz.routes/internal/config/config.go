package config

import (
	"errors"
	"fmt"
	"io/fs"
	"os"
	"strconv"
	"time"

	"github.com/joho/godotenv"
)

const EnvFile = ".env"

type Config struct {
	NatsURL          string
	ReadySubject     string
	DestroyedSubject string
	OutputPath       string
	GatewayDomain    string
	DebounceMs       int
}

func Load() (*Config, error) {
	if err := godotenv.Load(EnvFile); err != nil && !errors.Is(err, fs.ErrNotExist) {
		return nil, fmt.Errorf("load %s: %w", EnvFile, err)
	}

	cfg := &Config{
		NatsURL:          envOr("NATS_URL", "nats://localhost:4222"),
		ReadySubject:     envOr("NATS_READY_SUBJECT", "vm.ready"),
		DestroyedSubject: envOr("NATS_DESTROYED_SUBJECT", "vm.destroyed"),
		OutputPath:       envOr("OUTPUT_PATH", "/etc/traefik/dynamic/sandboxes.yml"),
		GatewayDomain:    envOr("GATEWAY_DOMAIN", "ssh.alcatraz.io"),
		DebounceMs:       500,
	}

	if v := os.Getenv("DEBOUNCE_MS"); v != "" {
		n, err := strconv.Atoi(v)
		if err != nil || n < 0 {
			return nil, fmt.Errorf("invalid DEBOUNCE_MS=%q", v)
		}
		cfg.DebounceMs = n
	}

	return cfg, nil
}

func (c *Config) Debounce() time.Duration {
	return time.Duration(c.DebounceMs) * time.Millisecond
}

func envOr(name, def string) string {
	if v := os.Getenv(name); v != "" {
		return v
	}
	return def
}
