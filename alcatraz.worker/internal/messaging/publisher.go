package messaging

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"alcatraz.worker/internal/metering"
	"github.com/nats-io/nats.go"
	"github.com/nats-io/nats.go/jetstream"
)

// Publisher fires post-boot events back to anyone subscribed (alcatraz.api,
// alcatraz.routes). It owns its own NATS connection rather than sharing the
// subscriber's — Subscriber's lifecycle is tied to Stop/Drain and the publish
// path uses Publish + Flush, so the two stay isolated.
//
// vm.ready and vm.destroyed travel over core NATS. vm.usage_sample and
// vm.usage_final go through JetStream so the API can ack-after-DB-commit and
// recover from outages without losing billing data.
type Publisher struct {
	nc                  *nats.Conn
	js                  jetstream.JetStream
	readySubject        string
	destroyedSubject    string
	usageSampleSubject  string
	usageFinalSubject   string
}

func NewPublisher(natsURL, readySubject, destroyedSubject, usageSampleSubject, usageFinalSubject string) (*Publisher, error) {
	if natsURL == "" {
		return nil, errors.New("nats url is empty")
	}
	nc, err := nats.Connect(natsURL)
	if err != nil {
		return nil, fmt.Errorf("connect: %w", err)
	}
	js, err := jetstream.New(nc)
	if err != nil {
		_ = nc.Drain()
		return nil, fmt.Errorf("jetstream: %w", err)
	}
	return &Publisher{
		nc:                 nc,
		js:                 js,
		readySubject:       readySubject,
		destroyedSubject:   destroyedSubject,
		usageSampleSubject: usageSampleSubject,
		usageFinalSubject:  usageFinalSubject,
	}, nil
}

// VMReadyInfo is the wire payload published on vm.ready. Optional fields use
// pointer types so unavailable values (e.g. when the Firecracker metadata API
// fails) round-trip as JSON null instead of bogus zeros.
type VMReadyInfo struct {
	// Group A — customer-facing.
	ID              string    `json:"id"`
	Host            string    `json:"host"`
	Port            int       `json:"port"`
	ActualVcpus     int       `json:"actual_vcpus"`
	ActualMemoryMib int       `json:"actual_memory_mib"`
	BootDurationMs  int64     `json:"boot_duration_ms"`
	ReadyAtUtc      time.Time `json:"ready_at_utc"`

	// Group B — ops/support.
	VmmVersion      *string `json:"vmm_version,omitempty"`
	VmmState        *string `json:"vmm_state,omitempty"`
	FirecrackerPid  *int    `json:"firecracker_pid,omitempty"`
	SocketPath      string  `json:"socket_path"`
	TapDevice       string  `json:"tap_device"`
	MacAddress      string  `json:"mac_address"`
	VmIp            string  `json:"vm_ip"`
	HostGatewayIp   string  `json:"host_gateway_ip"`
	NfsPort         int     `json:"nfs_port"`
	WorkerSlotIndex int     `json:"worker_slot_index"`
	RootfsPath      string  `json:"rootfs_path"`
	KernelPath      string  `json:"kernel_path"`

	// Group C — boot phase telemetry. Logged on the API side, not persisted.
	PhaseOverlayPrepMs int64 `json:"phase_overlay_prep_ms"`
	PhaseFcBootMs      int64 `json:"phase_fc_boot_ms"`
	PhaseSshdProbeMs   int64 `json:"phase_sshd_probe_ms"`
}

type vmDestroyedPayload struct {
	ID string `json:"id"`
}

func (p *Publisher) PublishVMReady(_ context.Context, info VMReadyInfo) error {
	data, err := json.Marshal(info)
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

// PublishUsageSample publishes a periodic usage sample via JetStream. The
// Nats-Msg-Id header lets the server dedupe redeliveries that occur inside
// the stream's dedup window (default 2 minutes).
func (p *Publisher) PublishUsageSample(ctx context.Context, payload metering.SamplePayload) error {
	data, err := json.Marshal(payload)
	if err != nil {
		return fmt.Errorf("marshal vm.usage_sample: %w", err)
	}
	msgID := fmt.Sprintf("%s|%d", payload.SandboxID, payload.SampledAtUtc.UnixNano())
	_, err = p.js.Publish(ctx, p.usageSampleSubject, data, jetstream.WithMsgID(msgID))
	if err != nil {
		return fmt.Errorf("publish vm.usage_sample: %w", err)
	}
	return nil
}

// PublishUsageFinal publishes the final usage record via JetStream. MsgID is
// the sandbox UUID so JetStream dedupes any duplicate finals server-side.
func (p *Publisher) PublishUsageFinal(ctx context.Context, payload metering.FinalPayload) error {
	data, err := json.Marshal(payload)
	if err != nil {
		return fmt.Errorf("marshal vm.usage_final: %w", err)
	}
	_, err = p.js.Publish(ctx, p.usageFinalSubject, data, jetstream.WithMsgID(payload.SandboxID))
	if err != nil {
		return fmt.Errorf("publish vm.usage_final: %w", err)
	}
	return nil
}

func (p *Publisher) Close() error {
	if p == nil || p.nc == nil {
		return nil
	}
	return p.nc.Drain()
}
