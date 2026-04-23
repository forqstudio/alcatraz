package nats

import (
	"encoding/json"
	"fmt"
	"log"

	"github.com/nats-io/nats.go"

	"alcatraz.worker/internal/vm"
)

type MessageHandler func(*Message) error

type Subscriber struct {
	nc         *nats.Conn
	sub        *nats.Subscription
	subject    string
	queueGroup string
	handler    MessageHandler
}

func NewSubscriber(
	natsUrl,
	natsSubject,
	natsQueueGroup string,
	messageHandler MessageHandler) (*Subscriber, error) {
	natsConnection, err := nats.Connect(natsUrl)
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

func (subscriber *Subscriber) Start() error {
	channel := make(chan *nats.Msg, 64)
	sub, err := subscriber.nc.ChanQueueSubscribe(
		subscriber.subject,
		subscriber.queueGroup,
		channel)

	if err != nil {
		return fmt.Errorf("failed to subscribe: %w", err)
	}
	subscriber.sub = sub

	go func() {
		for msg := range channel {
			subscriber.handleMessage(msg)
		}
	}()

	log.Printf("Subscribed to %s (queue: %s)", subscriber.subject, subscriber.queueGroup)
	return nil
}

func (subscriber *Subscriber) handleMessage(message *nats.Msg) {
	var msg Message
	if err := json.Unmarshal(message.Data, &msg); err != nil {
		log.Printf("Failed to parse request: %v", err)
		return
	}

	log.Printf("Received spawn request: %+v", msg)

	if err := subscriber.handler(&msg); err != nil {
		log.Printf("Failed to handle request: %v", err)
	}
}

func (subscriber *Subscriber) Stop() error {
	if subscriber.sub != nil {
		subscriber.sub.Unsubscribe()
	}
	if subscriber.nc != nil {
		subscriber.nc.Drain()
	}
	return nil
}

func (subscriber *Subscriber) URL() string {
	if subscriber.nc != nil {
		return subscriber.nc.ConnectedUrl()
	}
	return ""
}

func (subscriber *Subscriber) IsConnected() bool {
	return subscriber.nc != nil && subscriber.nc.IsConnected()
}

type Message struct {
	ID         string `json:"id,omitempty"`
	VCPUs      int    `json:"vcpus,omitempty"`
	MemoryMib  int    `json:"memory_mib,omitempty"`
	KernelArgs string `json:"kernel_args,omitempty"`
}

func (m *Message) ToVMRequest() *vm.Request {
	return &vm.Request{
		ID:         m.ID,
		VCPUs:      m.VCPUs,
		MemoryMib:  m.MemoryMib,
		KernelArgs: m.KernelArgs,
	}
}