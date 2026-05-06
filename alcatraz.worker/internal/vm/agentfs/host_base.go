package agentfs

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"sync/atomic"
	"syscall"

	sdk "github.com/tursodatabase/agentfs/sdk/go"
)

// HostBase implements sdk.BaseFS over a real host directory.
//
// Inodes are allocated lazily on Lookup; root is fixed at sdk.RootIno (= 1).
// (dev, ino) reverse mapping gives stable inode numbers across hardlinks.
type HostBase struct {
	root string

	mu      sync.RWMutex
	byIno   map[int64]string         // ino -> absolute path
	bySrc   map[srcKey]int64         // (host dev, host ino) -> our ino
	nextIno atomic.Int64             // next free ino, starts at 2
}

type srcKey struct {
	dev uint64
	ino uint64
}

// NewHostBase opens the directory and registers it as the root inode.
func NewHostBase(root string) (*HostBase, error) {
	abs, err := filepath.Abs(root)
	if err != nil {
		return nil, fmt.Errorf("absolute path: %w", err)
	}
	st, err := os.Lstat(abs)
	if err != nil {
		return nil, fmt.Errorf("lstat root: %w", err)
	}
	if !st.IsDir() {
		return nil, fmt.Errorf("base path is not a directory: %s", abs)
	}

	hb := &HostBase{
		root:  abs,
		byIno: make(map[int64]string),
		bySrc: make(map[srcKey]int64),
	}
	hb.nextIno.Store(2)
	hb.byIno[sdk.RootIno] = abs
	if sys := unixStat(st); sys != nil {
		hb.bySrc[srcKey{dev: uint64(sys.Dev), ino: sys.Ino}] = sdk.RootIno
	}
	return hb, nil
}

func (h *HostBase) pathOf(ino int64) (string, bool) {
	h.mu.RLock()
	defer h.mu.RUnlock()
	p, ok := h.byIno[ino]
	return p, ok
}

// allocIno returns the existing ino for (dev,ino) if known, otherwise mints a new one
// and registers the absolute path.
func (h *HostBase) allocIno(absPath string, sys *syscall.Stat_t) int64 {
	if sys != nil {
		key := srcKey{dev: uint64(sys.Dev), ino: sys.Ino}
		h.mu.RLock()
		if existing, ok := h.bySrc[key]; ok {
			h.mu.RUnlock()
			return existing
		}
		h.mu.RUnlock()
	}

	h.mu.Lock()
	defer h.mu.Unlock()
	if sys != nil {
		key := srcKey{dev: uint64(sys.Dev), ino: sys.Ino}
		if existing, ok := h.bySrc[key]; ok {
			return existing
		}
		ino := h.nextIno.Add(1) - 1
		h.bySrc[key] = ino
		h.byIno[ino] = absPath
		return ino
	}
	ino := h.nextIno.Add(1) - 1
	h.byIno[ino] = absPath
	return ino
}

func unixStat(fi os.FileInfo) *syscall.Stat_t {
	if fi == nil {
		return nil
	}
	if sys, ok := fi.Sys().(*syscall.Stat_t); ok {
		return sys
	}
	return nil
}

// statsFromFileInfo translates an os.FileInfo into sdk.Stats.
func statsFromFileInfo(ino int64, fi os.FileInfo) *sdk.Stats {
	mode := fi.Mode()
	var modeBits int64
	switch {
	case mode.IsDir():
		modeBits = sdk.S_IFDIR
	case mode&os.ModeSymlink != 0:
		modeBits = sdk.S_IFLNK
	case mode&os.ModeNamedPipe != 0:
		modeBits = sdk.S_IFIFO
	case mode&os.ModeSocket != 0:
		modeBits = sdk.S_IFSOCK
	case mode&os.ModeCharDevice != 0:
		modeBits = sdk.S_IFCHR
	case mode&os.ModeDevice != 0:
		modeBits = sdk.S_IFBLK
	default:
		modeBits = sdk.S_IFREG
	}
	modeBits |= int64(mode.Perm())

	stats := &sdk.Stats{
		Ino:   ino,
		Mode:  modeBits,
		Nlink: 1,
		Size:  fi.Size(),
	}
	if sys := unixStat(fi); sys != nil {
		stats.UID = int64(sys.Uid)
		stats.GID = int64(sys.Gid)
		stats.Nlink = int64(sys.Nlink)
		stats.Rdev = int64(sys.Rdev)
		stats.Atime = sys.Atim.Sec
		stats.AtimeNsec = sys.Atim.Nsec
		stats.Mtime = sys.Mtim.Sec
		stats.MtimeNsec = sys.Mtim.Nsec
		stats.Ctime = sys.Ctim.Sec
		stats.CtimeNsec = sys.Ctim.Nsec
	}
	return stats
}

// Stat implements sdk.BaseFS.
func (h *HostBase) Stat(_ context.Context, ino int64) (*sdk.Stats, error) {
	p, ok := h.pathOf(ino)
	if !ok {
		return nil, fmt.Errorf("ino %d unknown", ino)
	}
	fi, err := os.Lstat(p)
	if err != nil {
		return nil, err
	}
	return statsFromFileInfo(ino, fi), nil
}

// Lookup implements sdk.BaseFS.
func (h *HostBase) Lookup(_ context.Context, parentIno int64, name string) (*sdk.Stats, error) {
	parent, ok := h.pathOf(parentIno)
	if !ok {
		return nil, fmt.Errorf("parent ino %d unknown", parentIno)
	}
	p := filepath.Join(parent, name)
	fi, err := os.Lstat(p)
	if err != nil {
		return nil, err
	}
	ino := h.allocIno(p, unixStat(fi))
	return statsFromFileInfo(ino, fi), nil
}

// Readdir implements sdk.BaseFS.
func (h *HostBase) Readdir(_ context.Context, ino int64) ([]string, error) {
	p, ok := h.pathOf(ino)
	if !ok {
		return nil, fmt.Errorf("ino %d unknown", ino)
	}
	entries, err := os.ReadDir(p)
	if err != nil {
		return nil, err
	}
	names := make([]string, 0, len(entries))
	for _, e := range entries {
		names = append(names, e.Name())
	}
	return names, nil
}

// ReaddirPlus implements sdk.BaseFS.
func (h *HostBase) ReaddirPlus(_ context.Context, ino int64) ([]sdk.DirEntry, error) {
	p, ok := h.pathOf(ino)
	if !ok {
		return nil, fmt.Errorf("ino %d unknown", ino)
	}
	entries, err := os.ReadDir(p)
	if err != nil {
		return nil, err
	}
	out := make([]sdk.DirEntry, 0, len(entries))
	for _, e := range entries {
		full := filepath.Join(p, e.Name())
		fi, err := os.Lstat(full)
		if err != nil {
			continue
		}
		childIno := h.allocIno(full, unixStat(fi))
		out = append(out, sdk.DirEntry{
			Name:  e.Name(),
			Stats: statsFromFileInfo(childIno, fi),
		})
	}
	return out, nil
}

// ReadFile implements sdk.BaseFS.
func (h *HostBase) ReadFile(_ context.Context, ino int64) ([]byte, error) {
	p, ok := h.pathOf(ino)
	if !ok {
		return nil, fmt.Errorf("ino %d unknown", ino)
	}
	return os.ReadFile(p)
}

// Readlink implements sdk.BaseFS.
func (h *HostBase) Readlink(_ context.Context, ino int64) (string, error) {
	p, ok := h.pathOf(ino)
	if !ok {
		return "", fmt.Errorf("ino %d unknown", ino)
	}
	return os.Readlink(p)
}
