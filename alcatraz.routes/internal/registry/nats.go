package registry

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"

	"github.com/nats-io/nats.go"
)

// NATSConsumer subscribes to vm.ready and vm.destroyed (no queue group — every
// gateway replica needs the full table) and feeds the registry. On any change
// it calls notify so the writer can debounce + emit.
type NATSConsumer struct {
	nc               *nats.Conn
	reg              *Registry
	readySubject     string
	destroyedSubject string
	notify           func()
}

func NewNATSConsumer(
	nc *nats.Conn,
	reg *Registry,
	readySubject, destroyedSubject string,
	notify func(),
) *NATSConsumer {
	return &NATSConsumer{
		nc:               nc,
		reg:              reg,
		readySubject:     readySubject,
		destroyedSubject: destroyedSubject,
		notify:           notify,
	}
}

type readyPayload struct {
	ID   string `json:"id"`
	Host string `json:"host"`
	Port int    `json:"port"`
}

type destroyedPayload struct {
	ID string `json:"id"`
}

func (c *NATSConsumer) Run(ctx context.Context) error {
	readySub, err := c.nc.Subscribe(c.readySubject, c.handleReady)
	if err != nil {
		return fmt.Errorf("subscribe %s: %w", c.readySubject, err)
	}
	destroyedSub, err := c.nc.Subscribe(c.destroyedSubject, c.handleDestroyed)
	if err != nil {
		_ = readySub.Unsubscribe()
		return fmt.Errorf("subscribe %s: %w", c.destroyedSubject, err)
	}

	slog.Info("routes: subscribed",
		"ready_subject", c.readySubject,
		"destroyed_subject", c.destroyedSubject,
	)

	<-ctx.Done()

	_ = readySub.Unsubscribe()
	_ = destroyedSub.Unsubscribe()
	return ctx.Err()
}

func (c *NATSConsumer) handleReady(msg *nats.Msg) {
	var p readyPayload
	if err := json.Unmarshal(msg.Data, &p); err != nil {
		slog.Warn("routes: malformed vm.ready payload", "err", err)
		return
	}
	if p.ID == "" || p.Host == "" || p.Port <= 0 {
		slog.Warn("routes: incomplete vm.ready payload", "id", p.ID, "host", p.Host, "port", p.Port)
		return
	}
	if c.reg.Set(p.ID, p.Host, p.Port) {
		slog.Info("routes: route added/updated", "id", p.ID, "host", p.Host, "port", p.Port)
		c.notify()
	}
}

func (c *NATSConsumer) handleDestroyed(msg *nats.Msg) {
	var p destroyedPayload
	if err := json.Unmarshal(msg.Data, &p); err != nil {
		slog.Warn("routes: malformed vm.destroyed payload", "err", err)
		return
	}
	if p.ID == "" {
		return
	}
	if c.reg.Delete(p.ID) {
		slog.Info("routes: route removed", "id", p.ID)
		c.notify()
	}
}
