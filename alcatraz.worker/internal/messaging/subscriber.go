package messaging

import (
	"errors"
	"fmt"
	"log/slog"

	"github.com/nats-io/nats.go"
)

// MessageHandler receives the raw NATS message payload. The subscriber is
// payload-agnostic; deserialization is the caller's responsibility.
type MessageHandler func(data []byte) error

type Subscriber struct {
	nc         *nats.Conn
	sub        *nats.Subscription
	subject    string
	queueGroup string
	handler    MessageHandler
}

func NewSubscriber(
	natsURL,
	natsSubject,
	natsQueueGroup string,
	messageHandler MessageHandler) (*Subscriber, error) {
	natsConnection, err := nats.Connect(natsURL)
	if err != nil {
		return nil, fmt.Errorf("failed to connect to NATS: %w", err)
	}

	return &Subscriber{
		nc:         natsConnection,
		subject:    natsSubject,
		queueGroup: natsQueueGroup,
		handler:    messageHandler,
	}, nil
}

func (s *Subscriber) Start() error {
	channel := make(chan *nats.Msg, 64)
	sub, err := s.nc.ChanQueueSubscribe(s.subject, s.queueGroup, channel)
	if err != nil {
		return fmt.Errorf("failed to subscribe: %w", err)
	}
	s.sub = sub

	go func() {
		for msg := range channel {
			if err := s.handler(msg.Data); err != nil {
				slog.Error("Failed to handle request", "err", err)
			}
		}
	}()

	slog.Info("Subscribed to NATS", "subject", s.subject, "queue_group", s.queueGroup)
	return nil
}

// Stop unsubscribes and drains the NATS connection. Returns the joined error
// from the unsubscribe + drain calls so callers can decide how to surface
// shutdown failures.
func (s *Subscriber) Stop() error {
	var unsubErr, drainErr error
	if s.sub != nil {
		unsubErr = s.sub.Unsubscribe()
	}
	if s.nc != nil {
		drainErr = s.nc.Drain()
	}
	return errors.Join(unsubErr, drainErr)
}

func (s *Subscriber) URL() string {
	if s.nc != nil {
		return s.nc.ConnectedUrl()
	}
	return ""
}
