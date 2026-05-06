package vm

import (
	"os"
	"os/exec"
)

func FileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func RunCmd(name string, args ...string) error {
	cmd := exec.Command(name, args...)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	return cmd.Run()
}
