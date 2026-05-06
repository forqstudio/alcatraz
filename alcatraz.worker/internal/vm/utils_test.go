package vm

import (
	"testing"
)

func TestFileExists(t *testing.T) {
	if FileExists("/nonexistent/path/to/file") {
		t.Error("FileExists should return false for non-existent file")
	}

	if !FileExists("/proc/1/cmdline") {
		t.Error("FileExists should return true for existing file")
	}
}
