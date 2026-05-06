package logging

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"strings"
	"sync/atomic"
	"time"
)

const (
	seqQueueSize    = 1024
	seqBatchMax     = 100
	seqFlushTick    = 500 * time.Millisecond
	seqHTTPTimeout  = 5 * time.Second
	seqIngestSuffix = "/api/events/raw?clef"
)

// seqShared owns all the mutable, no-copy state. WithAttrs / WithGroup return
// new seqHandler values that share the same *seqShared.
type seqShared struct {
	url    string
	apiKey string
	level  slog.Level
	client *http.Client

	queue   chan map[string]any
	dropped atomic.Uint64

	ctx    context.Context
	cancel context.CancelFunc
	done   chan struct{}
}

type seqHandler struct {
	s      *seqShared
	attrs  []slog.Attr
	groups []string
}

func newSeqHandler(url, apiKey string, level slog.Level) *seqHandler {
	ctx, cancel := context.WithCancel(context.Background())
	s := &seqShared{
		url:    strings.TrimRight(url, "/") + seqIngestSuffix,
		apiKey: apiKey,
		level:  level,
		client: &http.Client{Timeout: seqHTTPTimeout},
		queue:  make(chan map[string]any, seqQueueSize),
		ctx:    ctx,
		cancel: cancel,
		done:   make(chan struct{}),
	}
	h := &seqHandler{s: s}
	go h.run()
	return h
}

func (h *seqHandler) Enabled(_ context.Context, level slog.Level) bool {
	return level >= h.s.level
}

func (h *seqHandler) Handle(_ context.Context, r slog.Record) error {
	payload := map[string]any{
		"@t": r.Time.UTC().Format(time.RFC3339Nano),
		"@m": r.Message,
	}
	if r.Level != slog.LevelInfo {
		payload["@l"] = clefLevel(r.Level)
	}
	for _, a := range h.attrs {
		flattenAttr(payload, h.groups, a)
	}
	r.Attrs(func(a slog.Attr) bool {
		flattenAttr(payload, h.groups, a)
		return true
	})

	select {
	case h.s.queue <- payload:
	default:
		h.s.dropped.Add(1)
	}
	return nil
}

func (h *seqHandler) WithAttrs(attrs []slog.Attr) slog.Handler {
	merged := make([]slog.Attr, 0, len(h.attrs)+len(attrs))
	merged = append(merged, h.attrs...)
	merged = append(merged, attrs...)
	return &seqHandler{s: h.s, attrs: merged, groups: h.groups}
}

func (h *seqHandler) WithGroup(name string) slog.Handler {
	if name == "" {
		return h
	}
	merged := make([]string, 0, len(h.groups)+1)
	merged = append(merged, h.groups...)
	merged = append(merged, name)
	return &seqHandler{s: h.s, attrs: h.attrs, groups: merged}
}

func (h *seqHandler) Close(ctx context.Context) {
	h.s.cancel()
	select {
	case <-h.s.done:
	case <-ctx.Done():
	}
}

func (h *seqHandler) run() {
	defer close(h.s.done)
	ticker := time.NewTicker(seqFlushTick)
	defer ticker.Stop()

	var batch [][]byte
	flush := func() {
		if len(batch) == 0 {
			return
		}
		h.send(batch)
		batch = batch[:0]
	}

	enqueue := func(p map[string]any) {
		line, err := json.Marshal(p)
		if err != nil {
			return
		}
		batch = append(batch, line)
		if len(batch) >= seqBatchMax {
			flush()
		}
	}

	for {
		select {
		case p := <-h.s.queue:
			enqueue(p)
		case <-ticker.C:
			flush()
		case <-h.s.ctx.Done():
			for {
				select {
				case p := <-h.s.queue:
					enqueue(p)
				default:
					flush()
					return
				}
			}
		}
	}
}

func (h *seqHandler) send(batch [][]byte) {
	var body bytes.Buffer
	for _, line := range batch {
		body.Write(line)
		body.WriteByte('\n')
	}
	req, err := http.NewRequest(http.MethodPost, h.s.url, &body)
	if err != nil {
		return
	}
	req.Header.Set("Content-Type", "application/vnd.serilog.clef")
	if h.s.apiKey != "" {
		req.Header.Set("X-Seq-ApiKey", h.s.apiKey)
	}
	resp, err := h.s.client.Do(req)
	if err != nil {
		return
	}
	_, _ = io.Copy(io.Discard, resp.Body)
	_ = resp.Body.Close()
}

func clefLevel(l slog.Level) string {
	switch {
	case l <= slog.LevelDebug:
		return "Debug"
	case l < slog.LevelWarn:
		return "Information"
	case l < slog.LevelError:
		return "Warning"
	default:
		return "Error"
	}
}

func flattenAttr(out map[string]any, groups []string, a slog.Attr) {
	a.Value = a.Value.Resolve()
	if a.Equal(slog.Attr{}) {
		return
	}

	if a.Value.Kind() == slog.KindGroup {
		sub := a.Value.Group()
		if len(sub) == 0 {
			return
		}
		nextGroups := groups
		if a.Key != "" {
			nextGroups = append(append([]string{}, groups...), a.Key)
		}
		for _, sa := range sub {
			flattenAttr(out, nextGroups, sa)
		}
		return
	}

	key := a.Key
	if len(groups) > 0 {
		key = strings.Join(groups, ".") + "." + key
	}

	if key == "error" || key == "err" {
		out["@x"] = fmt.Sprint(a.Value.Any())
		return
	}
	out[key] = a.Value.Any()
}
