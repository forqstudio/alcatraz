package nats

import (
	"os"

	"github.com/joho/godotenv"
)

const EnvFile = ".env"

const (
	DefaultNATSURL    = "nats://localhost:4222"
	DefaultSubject    = "vm.spawn"
	DefaultQueueGroup = "vm-workers"
)

type Config struct {
	URL        string
	Subject    string
	QueueGroup string
}

func LoadConfig() (*Config, error) {
	if err := godotenv.Load(EnvFile); err != nil {
		return nil, err
	}

	cfg := &Config{}

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
		URL:        DefaultNATSURL,
		Subject:    DefaultSubject,
		QueueGroup: DefaultQueueGroup,
	}
}