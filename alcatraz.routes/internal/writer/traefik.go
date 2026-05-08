package writer

import (
	"fmt"
	"os"
	"path/filepath"

	"alcatraz.routes/internal/registry"
	"gopkg.in/yaml.v3"
)

// traefikDoc is the shape Traefik's file provider expects under
// `tcp.routers` and `tcp.services`. We emit nothing under `http`/`tls` etc.
// so Traefik's static config controls those.
type traefikDoc struct {
	TCP traefikTCP `yaml:"tcp"`
}

type traefikTCP struct {
	Routers  map[string]traefikRouter  `yaml:"routers,omitempty"`
	Services map[string]traefikService `yaml:"services,omitempty"`
}

type traefikRouter struct {
	EntryPoints []string         `yaml:"entryPoints"`
	Rule        string           `yaml:"rule"`
	Service     string           `yaml:"service"`
	TLS         traefikRouterTLS `yaml:"tls"`
}

type traefikRouterTLS struct {
	CertResolver string                 `yaml:"certResolver"`
	Domains      []traefikRouterDomain `yaml:"domains,omitempty"`
}

type traefikRouterDomain struct {
	Main string `yaml:"main"`
}

type traefikService struct {
	LoadBalancer traefikLoadBalancer `yaml:"loadBalancer"`
}

type traefikLoadBalancer struct {
	Servers []traefikServer `yaml:"servers"`
}

type traefikServer struct {
	Address string `yaml:"address"`
}

// Render produces the YAML body for a snapshot of routes. gatewayDomain is the
// hostname all sandboxes share for the ACME cert (e.g. ssh.alcatraz.io); the
// per-sandbox SNI is matched separately by HostSNI rules.
func Render(entries []registry.Entry, gatewayDomain string) ([]byte, error) {
	doc := traefikDoc{TCP: traefikTCP{
		Routers:  make(map[string]traefikRouter, len(entries)),
		Services: make(map[string]traefikService, len(entries)),
	}}

	for _, e := range entries {
		key := "sb-" + e.ID
		doc.TCP.Routers[key] = traefikRouter{
			EntryPoints: []string{"wss"},
			Rule:        fmt.Sprintf("HostSNI(`%s`)", e.ID),
			Service:     key,
			TLS: traefikRouterTLS{
				CertResolver: "letsencrypt",
				// Pin ACME to the gateway domain; routers match SNI separately.
				Domains: []traefikRouterDomain{{Main: gatewayDomain}},
			},
		}
		doc.TCP.Services[key] = traefikService{
			LoadBalancer: traefikLoadBalancer{
				Servers: []traefikServer{{Address: fmt.Sprintf("%s:%d", e.Endpoint.Host, e.Endpoint.Port)}},
			},
		}
	}

	return yaml.Marshal(&doc)
}

// WriteAtomic writes data to path via a tmp+rename so Traefik's file watcher
// never sees a partial file.
func WriteAtomic(path string, data []byte) error {
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return fmt.Errorf("mkdir %s: %w", dir, err)
	}

	tmp, err := os.CreateTemp(dir, ".sandboxes.*.yml.tmp")
	if err != nil {
		return fmt.Errorf("create temp: %w", err)
	}
	tmpPath := tmp.Name()
	cleanupTmp := func() { _ = os.Remove(tmpPath) }

	if _, err := tmp.Write(data); err != nil {
		_ = tmp.Close()
		cleanupTmp()
		return fmt.Errorf("write temp: %w", err)
	}
	if err := tmp.Sync(); err != nil {
		_ = tmp.Close()
		cleanupTmp()
		return fmt.Errorf("sync temp: %w", err)
	}
	if err := tmp.Close(); err != nil {
		cleanupTmp()
		return fmt.Errorf("close temp: %w", err)
	}

	if err := os.Rename(tmpPath, path); err != nil {
		cleanupTmp()
		return fmt.Errorf("rename: %w", err)
	}
	return nil
}
