package vm

import (
	"context"
	"log/slog"
	"os/exec"
	"time"
)

// SweepFailedScopes clears any failed alcatraz-vm-*.scope units left behind
// by a previous worker run that exited uncleanly. systemd refuses to start a
// new transient unit with the same name while a failed one remains, so we
// reset them at worker startup. Best-effort: a missing systemd-run or a
// non-systemd host makes this a no-op.
func SweepFailedScopes() {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	cmd := exec.CommandContext(ctx, "systemctl", "reset-failed", "alcatraz-vm-*.scope")
	out, err := cmd.CombinedOutput()
	if err != nil {
		slog.Warn("scope sweep: systemctl reset-failed returned non-zero",
			"err", err,
			"output", string(out))
		return
	}
	slog.Info("scope sweep: reset-failed alcatraz-vm-*.scope")
}
