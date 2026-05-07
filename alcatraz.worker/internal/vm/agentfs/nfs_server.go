package agentfs

import (
	"context"
	"fmt"
	"log/slog"
	"net"
	"strconv"
	"sync"

	nfs "github.com/willscott/go-nfs"
	nfshelper "github.com/willscott/go-nfs/helpers"
)

// NFSServer is a long-lived NFSv3 server bound to a single AgentFS overlay.
// It implements the worker's vm.NFSProcess interface.
type NFSServer struct {
	listener net.Listener
	handle   *OverlayHandle
	done     chan struct{}
	err      error

	once sync.Once
}

// OpenAndServe opens the overlay for agentID and starts an NFSv3 server bound
// to bindIP:port. On a failure to start the server, the overlay is closed
// before returning. Mirrors http.ListenAndServe in shape.
func OpenAndServe(ctx context.Context, agentID, bindIP string, port int, rootfsPath, dataDir string) (*NFSServer, error) {
	handle, err := OpenOverlay(ctx, agentID, rootfsPath, dataDir)
	if err != nil {
		return nil, err
	}
	srv, err := StartNFS(handle, agentID, bindIP, port)
	if err != nil {
		_ = handle.Close()
		return nil, err
	}
	return srv, nil
}

// StartNFS binds an NFSv3 listener on bindIP:port and serves the overlay until Kill.
func StartNFS(handle *OverlayHandle, agentID, bindIP string, port int) (*NFSServer, error) {
	addr := net.JoinHostPort(bindIP, strconv.Itoa(port))
	ln, err := net.Listen("tcp", addr)
	if err != nil {
		return nil, fmt.Errorf("nfs listen on %s: %w", addr, err)
	}

	billy := newBillyFS(handle.overlay)
	authHandler := nfshelper.NewNullAuthHandler(billy)
	cachingHandler := nfshelper.NewCachingHandler(authHandler, 1024)

	srv := &NFSServer{
		listener: ln,
		handle:   handle,
		done:     make(chan struct{}),
	}
	go func() {
		defer close(srv.done)
		srv.err = nfs.Serve(ln, cachingHandler)
	}()
	slog.Info("AgentFS NFS listening", "agent_id", agentID, "addr", addr)
	return srv, nil
}

// Kill closes the listener and overlay. Idempotent.
func (s *NFSServer) Kill() error {
	var err error
	s.once.Do(func() {
		if s.listener != nil {
			err = s.listener.Close()
		}
		if s.handle != nil {
			_ = s.handle.Close()
		}
	})
	return err
}

// Wait blocks until the server goroutine has exited.
func (s *NFSServer) Wait() error {
	<-s.done
	return s.err
}
