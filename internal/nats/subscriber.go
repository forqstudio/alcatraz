package nats

import (
	"encoding/json"
	"fmt"
	"log"

	"github.com/nats-io/nats.go"

	"firecracker-agentfs/internal/config"
)

type MessageHandler func(*config.VMRequest) error

type Subscriber struct {
	nc        *nats.Conn
	sub       *nats.Subscription
	subject   string
	queueGroup string
	handler   MessageHandler
}

func NewSubscriber(url, subject, queueGroup string, handler MessageHandler) (*Subscriber, error) {
	nc, err := nats.Connect(url)
	if err != nil {
		return nil, fmt.Errorf("failed to connect to NATS: %w", err)
	}

	return &Subscriber{
		nc:         nc,
		subject:    subject,
		queueGroup: queueGroup,
		handler:    handler,
	}, nil
}

func (s *Subscriber) Start() error {
	ch := make(chan *nats.Msg, 64)
	sub, err := s.nc.ChanQueueSubscribe(s.subject, s.queueGroup, ch)
	if err != nil {
		return fmt.Errorf("failed to subscribe: %w", err)
	}
	s.sub = sub

	go func() {
		for msg := range ch {
			s.handleMessage(msg)
		}
	}()

	log.Printf("Subscribed to %s (queue: %s)", s.subject, s.queueGroup)
	return nil
}

func (s *Subscriber) handleMessage(msg *nats.Msg) {
	var req config.VMRequest
	if err := json.Unmarshal(msg.Data, &req); err != nil {
		log.Printf("Failed to parse request: %v", err)
		return
	}

	log.Printf("Received spawn request: %+v", req)

	if err := s.handler(&req); err != nil {
		log.Printf("Failed to handle request: %v", err)
	}
}

func (s *Subscriber) Stop() error {
	if s.sub != nil {
		s.sub.Unsubscribe()
	}
	if s.nc != nil {
		s.nc.Drain()
	}
	return nil
}

func (s *Subscriber) URL() string {
	if s.nc != nil {
		return s.nc.ConnectedUrl()
	}
	return ""
}

func (s *Subscriber) IsConnected() bool {
	return s.nc != nil && s.nc.IsConnected()
}