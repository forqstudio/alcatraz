package agentfs

import (
	"context"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

// TestPrepareAndServeNFS_Lifecycle exercises the full path: stamp -> Prepare ->
// OpenOverlay -> StartNFS -> dial -> Kill -> Wait. It uses the real production
// rootfs at ../alcatraz.core/rootfs.
func TestPrepareAndServeNFS_Lifecycle(t *testing.T) {
	rootfs, err := filepath.Abs("../../../../alcatraz.core/rootfs")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(rootfs); err != nil {
		t.Skipf("rootfs missing at %s: %v", rootfs, err)
	}

	dataDir := t.TempDir()
	agentID := "test-lifecycle"

	if err := PrepareOverlay(agentID, rootfs, dataDir); err != nil {
		t.Fatalf("PrepareOverlay: %v", err)
	}
	dbPath := filepath.Join(dataDir, agentID+".db")
	if _, err := os.Stat(dbPath); err != nil {
		t.Fatalf("expected db at %s: %v", dbPath, err)
	}
	stampPath := filepath.Join(dataDir, agentID+".base-stamp")
	if data, err := os.ReadFile(stampPath); err != nil {
		t.Fatalf("expected stamp at %s: %v", stampPath, err)
	} else if len(strings.TrimSpace(string(data))) != 64 {
		t.Errorf("stamp = %q (len %d), expected 64-char hex", string(data), len(data))
	}

	ctx := context.Background()
	handle, err := OpenOverlay(ctx, agentID, rootfs, dataDir)
	if err != nil {
		t.Fatalf("OpenOverlay: %v", err)
	}
	defer handle.Close()

	port := freePort(t)
	srv, err := StartNFS(handle, "127.0.0.1", port)
	if err != nil {
		t.Fatalf("StartNFS: %v", err)
	}

	// Confirm the listener is actually live.
	conn, err := net.DialTimeout("tcp", net.JoinHostPort("127.0.0.1", strconv.Itoa(port)), 2*time.Second)
	if err != nil {
		_ = srv.Kill()
		t.Fatalf("dial NFS port: %v", err)
	}
	conn.Close()

	if err := srv.Kill(); err != nil {
		t.Errorf("Kill: %v", err)
	}
	doneCh := make(chan error, 1)
	go func() { doneCh <- srv.Wait() }()
	select {
	case <-doneCh:
	case <-time.After(3 * time.Second):
		t.Fatal("NFSServer.Wait did not return after Kill")
	}

	// Second Kill should be idempotent.
	if err := srv.Kill(); err != nil {
		t.Errorf("second Kill returned err: %v", err)
	}
}

// TestPrepareOverlay_ReinitOnStampChange verifies that mutating the stamp file
// triggers a full DB rebuild on the next Prepare.
func TestPrepareOverlay_ReinitOnStampChange(t *testing.T) {
	rootfs, err := filepath.Abs("../../../../alcatraz.core/rootfs")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(rootfs); err != nil {
		t.Skipf("rootfs missing at %s: %v", rootfs, err)
	}

	dataDir := t.TempDir()
	agentID := "test-reinit"
	if err := PrepareOverlay(agentID, rootfs, dataDir); err != nil {
		t.Fatalf("PrepareOverlay 1: %v", err)
	}
	dbPath := filepath.Join(dataDir, agentID+".db")
	stampPath := filepath.Join(dataDir, agentID+".base-stamp")
	st1, err := os.Stat(dbPath)
	if err != nil {
		t.Fatal(err)
	}
	// Force a stamp mismatch by writing a bogus value and waiting one tick so
	// the new mtime is observably different.
	if err := os.WriteFile(stampPath, []byte("0000000000000000000000000000000000000000000000000000000000000000"), 0o644); err != nil {
		t.Fatal(err)
	}
	time.Sleep(10 * time.Millisecond)
	if err := PrepareOverlay(agentID, rootfs, dataDir); err != nil {
		t.Fatalf("PrepareOverlay 2: %v", err)
	}
	st2, err := os.Stat(dbPath)
	if err != nil {
		t.Fatal(err)
	}
	if !st2.ModTime().After(st1.ModTime()) && st1.Size() == st2.Size() {
		// The DB was not recreated (mtime should advance from rebuild).
		t.Errorf("expected DB rebuild on stamp mismatch; mtimes %v -> %v", st1.ModTime(), st2.ModTime())
	}
}

func freePort(t *testing.T) int {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	port := ln.Addr().(*net.TCPAddr).Port
	ln.Close()
	return port
}
