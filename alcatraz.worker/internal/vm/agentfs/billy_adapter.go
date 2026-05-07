package agentfs

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/go-git/go-billy/v5"
	sdk "github.com/tursodatabase/agentfs/sdk/go"
)

// billyFS adapts an *sdk.OverlayFS to billy.Filesystem so it can be served by
// github.com/willscott/go-nfs.
//
// File handles load the full file into memory on Open and flush back on Close
// of writable handles. This is wasteful for large files but correct, and
// matches the documented v1 perf debt — see docs/remove-cli-commands.md.
type billyFS struct {
	overlay *sdk.OverlayFS
	root    string
}

func newBillyFS(overlay *sdk.OverlayFS) *billyFS {
	return &billyFS{overlay: overlay, root: "/"}
}

// --- billy.Basic ---------------------------------------------------------

func (f *billyFS) Create(filename string) (billy.File, error) {
	return f.OpenFile(filename, os.O_RDWR|os.O_CREATE|os.O_TRUNC, 0o644)
}

func (f *billyFS) Open(filename string) (billy.File, error) {
	return f.OpenFile(filename, os.O_RDONLY, 0)
}

func (f *billyFS) OpenFile(filename string, flag int, perm os.FileMode) (billy.File, error) {
	clean := normalizePath(filename)
	ctx := context.Background()

	exists := false
	if _, err := f.overlay.LookupPath(ctx, clean); err == nil {
		exists = true
	}

	if !exists {
		if flag&os.O_CREATE == 0 {
			return nil, &os.PathError{Op: "open", Path: filename, Err: os.ErrNotExist}
		}
		if err := f.overlay.WriteFile(ctx, clean, nil, int64(sdk.S_IFREG|(perm&0o7777))); err != nil {
			return nil, &os.PathError{Op: "open", Path: filename, Err: err}
		}
	} else if flag&(os.O_CREATE|os.O_EXCL) == (os.O_CREATE | os.O_EXCL) {
		return nil, &os.PathError{Op: "open", Path: filename, Err: os.ErrExist}
	}

	var data []byte
	if flag&os.O_TRUNC == 0 {
		b, err := f.overlay.ReadFile(ctx, clean)
		if err != nil {
			b = nil
		}
		data = b
	}

	bf := &billyFile{
		fs:       f,
		path:     clean,
		mode:     perm,
		readable: flag&os.O_WRONLY == 0,
		writable: flag&os.O_RDWR != 0 || flag&os.O_WRONLY != 0,
		appending: flag&os.O_APPEND != 0,
		buf:      append([]byte(nil), data...),
	}
	if bf.appending {
		bf.pos = int64(len(bf.buf))
	}
	return bf, nil
}

func (f *billyFS) Stat(filename string) (os.FileInfo, error) {
	clean := normalizePath(filename)
	stats, err := f.overlay.LookupPath(context.Background(), clean)
	if err != nil {
		return nil, &os.PathError{Op: "stat", Path: filename, Err: translateErr(err)}
	}
	return &fileInfo{name: path.Base(clean), stats: stats}, nil
}

func (f *billyFS) Rename(oldpath, newpath string) error {
	return f.overlay.Rename(context.Background(), normalizePath(oldpath), normalizePath(newpath))
}

func (f *billyFS) Remove(filename string) error {
	clean := normalizePath(filename)
	ctx := context.Background()
	stats, err := f.overlay.LookupPath(ctx, clean)
	if err != nil {
		return &os.PathError{Op: "remove", Path: filename, Err: translateErr(err)}
	}
	if stats.IsDir() {
		return f.overlay.Rmdir(ctx, clean)
	}
	return f.overlay.Unlink(ctx, clean)
}

func (f *billyFS) Join(elem ...string) string {
	return path.Join(elem...)
}

// --- billy.TempFile ------------------------------------------------------

func (f *billyFS) TempFile(dir, prefix string) (billy.File, error) {
	if dir == "" {
		dir = "/tmp"
	}
	name := fmt.Sprintf("%s%d-%d", prefix, time.Now().UnixNano(), pid())
	full := path.Join(dir, name)
	return f.OpenFile(full, os.O_RDWR|os.O_CREATE|os.O_EXCL, 0o600)
}

// --- billy.Dir -----------------------------------------------------------

func (f *billyFS) ReadDir(p string) ([]os.FileInfo, error) {
	clean := normalizePath(p)
	ctx := context.Background()
	stats, err := f.overlay.LookupPath(ctx, clean)
	if err != nil {
		return nil, &os.PathError{Op: "readdir", Path: p, Err: translateErr(err)}
	}
	entries, err := f.overlay.ReaddirPlus(ctx, stats.Ino)
	if err != nil {
		return nil, err
	}
	out := make([]os.FileInfo, 0, len(entries))
	for _, e := range entries {
		out = append(out, &fileInfo{name: e.Name, stats: e.Stats})
	}
	return out, nil
}

func (f *billyFS) MkdirAll(filename string, perm os.FileMode) error {
	return f.overlay.MkdirAll(context.Background(), normalizePath(filename), int64(sdk.S_IFDIR|(perm&0o7777)))
}

// --- billy.Symlink -------------------------------------------------------

func (f *billyFS) Lstat(filename string) (os.FileInfo, error) {
	return f.Stat(filename) // OverlayFS.LookupPath does not follow symlinks
}

func (f *billyFS) Symlink(target, link string) error {
	return f.overlay.Symlink(context.Background(), target, normalizePath(link))
}

func (f *billyFS) Readlink(link string) (string, error) {
	return f.overlay.Readlink(context.Background(), normalizePath(link))
}

// --- billy.Chroot --------------------------------------------------------

func (f *billyFS) Chroot(p string) (billy.Filesystem, error) {
	return nil, errors.New("chroot not supported")
}

func (f *billyFS) Root() string { return f.root }

// --- File ----------------------------------------------------------------

type billyFile struct {
	fs        *billyFS
	path      string
	mode      os.FileMode
	readable  bool
	writable  bool
	appending bool

	mu      sync.Mutex
	buf     []byte
	pos     int64
	dirty   bool
	closed  bool
}

func (b *billyFile) Name() string { return b.path }

func (b *billyFile) Read(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if !b.readable {
		return 0, os.ErrPermission
	}
	if b.pos >= int64(len(b.buf)) {
		return 0, io.EOF
	}
	n := copy(p, b.buf[b.pos:])
	b.pos += int64(n)
	return n, nil
}

func (b *billyFile) ReadAt(p []byte, off int64) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if !b.readable {
		return 0, os.ErrPermission
	}
	if off >= int64(len(b.buf)) {
		return 0, io.EOF
	}
	n := copy(p, b.buf[off:])
	if n < len(p) {
		return n, io.EOF
	}
	return n, nil
}

func (b *billyFile) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if !b.writable {
		return 0, os.ErrPermission
	}
	if b.appending {
		b.pos = int64(len(b.buf))
	}
	end := b.pos + int64(len(p))
	if end > int64(len(b.buf)) {
		grown := make([]byte, end)
		copy(grown, b.buf)
		b.buf = grown
	}
	n := copy(b.buf[b.pos:], p)
	b.pos += int64(n)
	b.dirty = true
	return n, nil
}

func (b *billyFile) Seek(offset int64, whence int) (int64, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	var newPos int64
	switch whence {
	case io.SeekStart:
		newPos = offset
	case io.SeekCurrent:
		newPos = b.pos + offset
	case io.SeekEnd:
		newPos = int64(len(b.buf)) + offset
	default:
		return 0, fmt.Errorf("bad whence: %d", whence)
	}
	if newPos < 0 {
		return 0, fmt.Errorf("negative position")
	}
	b.pos = newPos
	return b.pos, nil
}

func (b *billyFile) Truncate(size int64) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	if !b.writable {
		return os.ErrPermission
	}
	if size < int64(len(b.buf)) {
		b.buf = b.buf[:size]
	} else if size > int64(len(b.buf)) {
		grown := make([]byte, size)
		copy(grown, b.buf)
		b.buf = grown
	}
	b.dirty = true
	return nil
}

func (b *billyFile) Lock() error   { return nil }
func (b *billyFile) Unlock() error { return nil }

func (b *billyFile) Close() error {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.closed {
		return nil
	}
	b.closed = true
	if b.writable && b.dirty {
		mode := int64(sdk.S_IFREG | (b.mode & 0o7777))
		if mode&0o7777 == 0 {
			mode = int64(sdk.S_IFREG | 0o644)
		}
		return b.fs.overlay.WriteFile(context.Background(), b.path, b.buf, mode)
	}
	return nil
}

// --- helpers -------------------------------------------------------------

type fileInfo struct {
	name  string
	stats *sdk.Stats
}

func (fi *fileInfo) Name() string { return fi.name }
func (fi *fileInfo) Size() int64  { return fi.stats.Size }
func (fi *fileInfo) Mode() os.FileMode {
	perm := os.FileMode(fi.stats.Mode & 0o777)
	switch fi.stats.Mode & sdk.S_IFMT {
	case sdk.S_IFDIR:
		perm |= os.ModeDir
	case sdk.S_IFLNK:
		perm |= os.ModeSymlink
	case sdk.S_IFIFO:
		perm |= os.ModeNamedPipe
	case sdk.S_IFSOCK:
		perm |= os.ModeSocket
	case sdk.S_IFCHR:
		perm |= os.ModeCharDevice | os.ModeDevice
	case sdk.S_IFBLK:
		perm |= os.ModeDevice
	}
	return perm
}
func (fi *fileInfo) ModTime() time.Time { return time.Unix(fi.stats.Mtime, fi.stats.MtimeNsec) }
func (fi *fileInfo) IsDir() bool        { return fi.stats.IsDir() }

// Sys returns a *syscall.Stat_t so go-nfs can read uid/gid/nlink/etc.
func (fi *fileInfo) Sys() interface{} {
	return &syscall.Stat_t{
		Dev:   0,
		Ino:   uint64(fi.stats.Ino),
		Mode:  uint32(fi.stats.Mode),
		Nlink: uint64(fi.stats.Nlink),
		Uid:   uint32(fi.stats.UID),
		Gid:   uint32(fi.stats.GID),
		Rdev:  uint64(fi.stats.Rdev),
		Size:  fi.stats.Size,
		Atim:  syscall.Timespec{Sec: fi.stats.Atime, Nsec: fi.stats.AtimeNsec},
		Mtim:  syscall.Timespec{Sec: fi.stats.Mtime, Nsec: fi.stats.MtimeNsec},
		Ctim:  syscall.Timespec{Sec: fi.stats.Ctime, Nsec: fi.stats.CtimeNsec},
	}
}

func normalizePath(p string) string {
	if p == "" {
		return "/"
	}
	cleaned := path.Clean(p)
	if !strings.HasPrefix(cleaned, "/") {
		cleaned = "/" + cleaned
	}
	return cleaned
}

// errSubstrings lists the substrings translateErr matches against. This is
// brittle — any wording change in the AgentFS SDK silently breaks the
// classification.
//
// FIXME: switch to errors.Is against typed sentinels exported by the SDK once
// the SDK provides them.
var (
	errNotExistSubstrings = []string{"no such", "not found", "ENOENT"}
	errExistSubstrings    = []string{"exists", "EEXIST"}
)

func translateErr(err error) error {
	if err == nil {
		return nil
	}
	msg := err.Error()
	for _, s := range errNotExistSubstrings {
		if strings.Contains(msg, s) {
			return os.ErrNotExist
		}
	}
	for _, s := range errExistSubstrings {
		if strings.Contains(msg, s) {
			return os.ErrExist
		}
	}
	return err
}

func pid() int { return os.Getpid() }
