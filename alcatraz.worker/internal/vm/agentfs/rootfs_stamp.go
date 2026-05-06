package agentfs

import (
	"crypto/sha256"
	"encoding/hex"
	"io"
	"os"
	"path/filepath"
)

// RootfsStamp returns the hex-encoded sha256 of <rootfsPath>/etc/alcatraz-release.
// Returns ("", nil) if the file does not exist — callers treat that as "no stamp".
func RootfsStamp(rootfsPath string) (string, error) {
	path := filepath.Join(rootfsPath, "etc/alcatraz-release")
	f, err := os.Open(path)
	if err != nil {
		if os.IsNotExist(err) {
			return "", nil
		}
		return "", err
	}
	defer f.Close()

	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}
