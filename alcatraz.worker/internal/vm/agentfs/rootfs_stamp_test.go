package agentfs

import (
	"crypto/sha256"
	"encoding/hex"
	"os"
	"path/filepath"
	"testing"
)

func TestRootfsStamp(t *testing.T) {
	root := t.TempDir()
	etc := filepath.Join(root, "etc")
	if err := os.MkdirAll(etc, 0o755); err != nil {
		t.Fatal(err)
	}
	contents := []byte("v1\n")
	if err := os.WriteFile(filepath.Join(etc, "alcatraz-release"), contents, 0o644); err != nil {
		t.Fatal(err)
	}
	hash, err := RootfsStamp(root)
	if err != nil {
		t.Fatalf("RootfsStamp: %v", err)
	}
	sum := sha256.Sum256(contents)
	want := hex.EncodeToString(sum[:])
	if hash != want {
		t.Errorf("RootfsStamp = %q, want %q", hash, want)
	}
	if len(hash) != 64 {
		t.Errorf("hash length = %d, want 64", len(hash))
	}
}

func TestRootfsStamp_Missing(t *testing.T) {
	root := t.TempDir()
	hash, err := RootfsStamp(root)
	if err != nil {
		t.Fatalf("RootfsStamp on missing: %v", err)
	}
	if hash != "" {
		t.Errorf("expected empty stamp for missing file, got %q", hash)
	}
}
