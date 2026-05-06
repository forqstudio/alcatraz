package agentfs

import (
	"context"
	"os"
	"path/filepath"
	"sort"
	"testing"

	sdk "github.com/tursodatabase/agentfs/sdk/go"
)

func TestHostBase_BasicOps(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "hello.txt"), []byte("world"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(dir, "sub"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink("hello.txt", filepath.Join(dir, "link")); err != nil {
		t.Fatal(err)
	}

	hb, err := NewHostBase(dir)
	if err != nil {
		t.Fatalf("NewHostBase: %v", err)
	}

	ctx := context.Background()

	rootStats, err := hb.Stat(ctx, sdk.RootIno)
	if err != nil {
		t.Fatalf("Stat root: %v", err)
	}
	if !rootStats.IsDir() {
		t.Errorf("root should be a directory, mode=%o", rootStats.Mode)
	}

	names, err := hb.Readdir(ctx, sdk.RootIno)
	if err != nil {
		t.Fatalf("Readdir: %v", err)
	}
	sort.Strings(names)
	want := []string{"hello.txt", "link", "sub"}
	if len(names) != len(want) {
		t.Fatalf("Readdir got %v, want %v", names, want)
	}
	for i := range want {
		if names[i] != want[i] {
			t.Errorf("Readdir[%d] = %q, want %q", i, names[i], want[i])
		}
	}

	helloStats, err := hb.Lookup(ctx, sdk.RootIno, "hello.txt")
	if err != nil {
		t.Fatalf("Lookup hello: %v", err)
	}
	if !helloStats.IsRegularFile() {
		t.Errorf("hello.txt mode=%o, want regular file", helloStats.Mode)
	}

	data, err := hb.ReadFile(ctx, helloStats.Ino)
	if err != nil {
		t.Fatalf("ReadFile: %v", err)
	}
	if string(data) != "world" {
		t.Errorf("ReadFile = %q, want %q", string(data), "world")
	}

	linkStats, err := hb.Lookup(ctx, sdk.RootIno, "link")
	if err != nil {
		t.Fatalf("Lookup link: %v", err)
	}
	if !linkStats.IsSymlink() {
		t.Errorf("link mode=%o, want symlink", linkStats.Mode)
	}
	target, err := hb.Readlink(ctx, linkStats.Ino)
	if err != nil {
		t.Fatalf("Readlink: %v", err)
	}
	if target != "hello.txt" {
		t.Errorf("Readlink = %q, want %q", target, "hello.txt")
	}
}

func TestHostBase_HardlinkDedup(t *testing.T) {
	dir := t.TempDir()
	primary := filepath.Join(dir, "a.txt")
	if err := os.WriteFile(primary, []byte("data"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Link(primary, filepath.Join(dir, "b.txt")); err != nil {
		t.Skipf("hardlink not supported: %v", err)
	}

	hb, err := NewHostBase(dir)
	if err != nil {
		t.Fatal(err)
	}

	ctx := context.Background()
	a, err := hb.Lookup(ctx, sdk.RootIno, "a.txt")
	if err != nil {
		t.Fatal(err)
	}
	b, err := hb.Lookup(ctx, sdk.RootIno, "b.txt")
	if err != nil {
		t.Fatal(err)
	}

	if a.Ino != b.Ino {
		t.Errorf("expected same inode for hardlinked files, got %d and %d", a.Ino, b.Ino)
	}
}
