package registry

import "sync"

type Endpoint struct {
	Host string
	Port int
}

type Entry struct {
	ID       string
	Endpoint Endpoint
}

// Registry maps a sandbox UUID to its reachable backend (host:port).
// Concurrency-safe; a single writer goroutine isn't assumed.
type Registry struct {
	mu      sync.RWMutex
	entries map[string]Endpoint
}

func New() *Registry {
	return &Registry{entries: make(map[string]Endpoint)}
}

// Set returns true if the entry was added or changed.
func (r *Registry) Set(id, host string, port int) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	prev, ok := r.entries[id]
	next := Endpoint{Host: host, Port: port}
	if ok && prev == next {
		return false
	}
	r.entries[id] = next
	return true
}

// Delete returns true if an entry was removed.
func (r *Registry) Delete(id string) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if _, ok := r.entries[id]; !ok {
		return false
	}
	delete(r.entries, id)
	return true
}

// Snapshot returns a sorted copy by ID for stable file output.
func (r *Registry) Snapshot() []Entry {
	r.mu.RLock()
	defer r.mu.RUnlock()
	out := make([]Entry, 0, len(r.entries))
	for id, ep := range r.entries {
		out = append(out, Entry{ID: id, Endpoint: ep})
	}
	sortEntries(out)
	return out
}

func (r *Registry) Len() int {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return len(r.entries)
}
