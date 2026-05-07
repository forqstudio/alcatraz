package messaging

import (
	"testing"
)

func TestDefaultConfig(t *testing.T) {
	cfg := DefaultConfig()

	if cfg.URL != DefaultURL {
		t.Errorf("URL = %s, want %s", cfg.URL, DefaultURL)
	}
	if cfg.Subject != DefaultSubject {
		t.Errorf("Subject = %s, want %s", cfg.Subject, DefaultSubject)
	}
	if cfg.QueueGroup != DefaultQueueGroup {
		t.Errorf("QueueGroup = %s, want %s", cfg.QueueGroup, DefaultQueueGroup)
	}
}
