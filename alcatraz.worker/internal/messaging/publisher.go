package messaging

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/nats-io/nats.go"
)

// Publisher fires post-boot events back to anyone subscribed (alcatraz.api,
// alcatraz.routes). It owns its own NATS connection rather than sharing the
// subscriber's — Subscriber's lifecycle is tied to Stop/Drain and the publish
// path uses Publish + Flush, so the two stay isolated.
type Publisher struct {
	nc               *nats.Conn
	readySubject     string
	destroyedSubject string
}

func NewPublisher(natsURL, readySubject, destroyedSubject string) (*Publisher, error) {
	if natsURL == "" {
		return nil, errors.New("nats url is empty")
	}
	nc, err := nats.Connect(natsURL)
	if err != nil {
		return nil, fmt.Errorf("connect: %w", err)
	}
	return &Publisher{
		nc:               nc,
		readySubject:     readySubject,
		destroyedSubject: destroyedSubject,
	}, nil
}

type vmReadyPayload struct {
	ID   string `json:"id"`
	Host string `json:"host"`
	Port int    `json:"port"`
}

type vmDestroyedPayload struct {
	ID string `json:"id"`
}

func (p *Publisher) PublishVMReady(_ context.Context, id, host string, port int) error {
	data, err := json.Marshal(vmReadyPayload{ID: id, Host: host, Port: port})
	if err != nil {
		return fmt.Errorf("marshal vm.ready: %w", err)
	}
	if err := p.nc.Publish(p.readySubject, data); err != nil {
		return fmt.Errorf("publish vm.ready: %w", err)
	}
	return p.nc.Flush()
}

func (p *Publisher) PublishVMDestroyed(_ context.Context, id string) error {
	data, err := json.Marshal(vmDestroyedPayload{ID: id})
	if err != nil {
		return fmt.Errorf("marshal vm.destroyed: %w", err)
	}
	if err := p.nc.Publish(p.destroyedSubject, data); err != nil {
		return fmt.Errorf("publish vm.destroyed: %w", err)
	}
	return p.nc.Flush()
}

func (p *Publisher) Close() error {
	if p == nil || p.nc == nil {
		return nil
	}
	return p.nc.Drain()
}
