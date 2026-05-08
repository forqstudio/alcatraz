package writer

import (
	"strings"
	"testing"

	"alcatraz.routes/internal/registry"
	"gopkg.in/yaml.v3"
)

func TestRender_Empty(t *testing.T) {
	body, err := Render(nil, "ssh.alcatraz.io")
	if err != nil {
		t.Fatalf("render: %v", err)
	}
	// Round-trip through yaml to make sure it parses, even if empty.
	var doc traefikDoc
	if err := yaml.Unmarshal(body, &doc); err != nil {
		t.Fatalf("parse: %v\n%s", err, body)
	}
}

func TestRender_OneRoute(t *testing.T) {
	id := "11111111-2222-3333-4444-555555555555"
	entries := []registry.Entry{
		{ID: id, Endpoint: registry.Endpoint{Host: "172.16.0.10", Port: 22}},
	}
	body, err := Render(entries, "ssh.alcatraz.io")
	if err != nil {
		t.Fatalf("render: %v", err)
	}
	out := string(body)
	for _, want := range []string{
		"sb-" + id,
		"HostSNI(`" + id + "`)",
		"172.16.0.10:22",
		"ssh.alcatraz.io",
		"letsencrypt",
		"wss",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("expected output to contain %q\n--- output ---\n%s", want, out)
		}
	}
}

func TestRender_StableOrder(t *testing.T) {
	entries := []registry.Entry{
		{ID: "bbbb", Endpoint: registry.Endpoint{Host: "10.0.0.2", Port: 22}},
		{ID: "aaaa", Endpoint: registry.Endpoint{Host: "10.0.0.1", Port: 22}},
	}
	body1, _ := Render(entries, "ssh.alcatraz.io")
	body2, _ := Render(entries, "ssh.alcatraz.io")
	if string(body1) != string(body2) {
		t.Errorf("Render is not deterministic for the same input")
	}
}
