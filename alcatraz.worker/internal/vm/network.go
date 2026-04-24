package vm

import (
	"fmt"
	"os/exec"
	"strings"
)

type NetworkSetupFunc func(VirtualMachineInfo, int) error

// SetupTap creates a TAP device for the VM and configures networking.
// It sets up:
//   - TAP device with host IP
//   - IP forwarding
//   - NAT (MASQUERADE) for internet access
//   - FORWARD rules for VM ↔ internet
//   - DROP rules to block intra-VM and cross-VM traffic
//
// The maxSlots parameter is the total number of VM slots (used to generate cross-VM isolation rules).
func SetupTap(virtualMachineInfo VirtualMachineInfo, maxSlots int) error {
	tapDev := virtualMachineInfo.GetTapDev()
	hostIP := virtualMachineInfo.GetHostTapIP()
	subnet := virtualMachineInfo.GetSubnet()

	if err := RunCmd("ip", "link", "del", tapDev); err != nil {
		fmt.Printf("warning: could not delete tap device: %v\n", err)
	}

	if err := RunCmd("ip", "tuntap", "add", "dev", tapDev, "mode", "tap"); err != nil {
		return fmt.Errorf("failed to create tap device: %w", err)
	}

	if err := RunCmd("ip", "addr", "add", fmt.Sprintf("%s/24", hostIP), "dev", tapDev); err != nil {
		return fmt.Errorf("failed to assign IP to tap: %w", err)
	}

	if err := RunCmd("ip", "link", "set", tapDev, "up"); err != nil {
		return fmt.Errorf("failed to bring up tap: %w", err)
	}

	if err := RunCmd("sysctl", "-w", "net.ipv4.ip_forward=1"); err != nil {
		return fmt.Errorf("failed to enable IP forward: %w", err)
	}

	if err := RunCmd("sysctl", "-w", fmt.Sprintf("net.ipv4.conf.%s.route_localnet=1", tapDev)); err != nil {
		return fmt.Errorf("failed to enable route_localnet: %w", err)
	}

	hostIface, err := GetHostIface()
	if err != nil {
		hostIface = "eth0"
	}

	if err := RunCmd("iptables", "-t", "nat", "-A", "POSTROUTING", "-s", subnet, "-o", hostIface, "-j", "MASQUERADE"); err != nil {
		return fmt.Errorf("failed to add NAT rule: %w", err)
	}

	if err := RunCmd("iptables", "-A", "FORWARD", "-i", hostIface, "-o", tapDev, "-m", "state", "--state", "RELATED,ESTABLISHED", "-j", "ACCEPT"); err != nil {
		return fmt.Errorf("failed to add forward rule 1: %w", err)
	}

	if err := RunCmd("iptables", "-A", "FORWARD", "-i", tapDev, "-o", hostIface, "-j", "ACCEPT"); err != nil {
		return fmt.Errorf("failed to add forward rule 2: %w", err)
	}

	if err := RunCmd("iptables", "-A", "FORWARD", "-i", tapDev, "-o", tapDev, "-j", "DROP"); err != nil {
		return fmt.Errorf("failed to add intra-VM drop rule: %w", err)
	}

	for i := 0; i < maxSlots; i++ {
		otherTap := fmt.Sprintf("fc-tap%d", i)
		if otherTap != tapDev {
			if err := RunCmd("iptables", "-A", "FORWARD", "-i", tapDev, "-o", otherTap, "-j", "DROP"); err != nil {
				fmt.Printf("warning: could not add cross-VM drop rule for %s: %v\n", otherTap, err)
			}
			if err := RunCmd("iptables", "-A", "FORWARD", "-i", otherTap, "-o", tapDev, "-j", "DROP"); err != nil {
				fmt.Printf("warning: could not add cross-VM drop rule for %s: %v\n", otherTap, err)
			}
		}
	}

	return nil
}

// CleanupTap removes network configuration for a VM.
// It removes:
//   - NAT (MASQUERADE) rules
//   - FORWARD ACCEPT rules
//   - Cross-VM isolation DROP rules
//   - The TAP device
func CleanupTap(virtualMachineInfo VirtualMachineInfo, maxSlots int) {
	tapDev := virtualMachineInfo.GetTapDev()
	subnet := virtualMachineInfo.GetSubnet()

	hostIface, _ := GetHostIface()
	RunCmd("iptables", "-t", "nat", "-D", "POSTROUTING", "-s", subnet, "-o", hostIface, "-j", "MASQUERADE")
	RunCmd("iptables", "-D", "FORWARD", "-i", hostIface, "-o", tapDev, "-m", "state", "--state", "RELATED,ESTABLISHED", "-j", "ACCEPT")
	RunCmd("iptables", "-D", "FORWARD", "-i", tapDev, "-o", hostIface, "-j", "ACCEPT")
	RunCmd("iptables", "-D", "FORWARD", "-i", tapDev, "-o", tapDev, "-j", "DROP")

	for i := 0; i < maxSlots; i++ {
		otherTap := fmt.Sprintf("fc-tap%d", i)
		if otherTap != tapDev {
			RunCmd("iptables", "-D", "FORWARD", "-i", tapDev, "-o", otherTap, "-j", "DROP")
			RunCmd("iptables", "-D", "FORWARD", "-i", otherTap, "-o", tapDev, "-j", "DROP")
		}
	}

	RunCmd("ip", "link", "del", tapDev)
}

// GetHostIface returns the default host network interface name (e.g., eth0).
// Falls back to "eth0" if detection fails.
func GetHostIface() (string, error) {
	cmd := exec.Command("ip", "route", "show", "default")
	out, err := cmd.Output()
	if err != nil {
		return "", err
	}

	fields := string(out)
	for _, f := range strings.Fields(fields) {
		if f == "dev" {
			continue
		}
		if !strings.HasPrefix(f, "-") && !strings.HasPrefix(f, "default") {
			return f, nil
		}
	}

	return "eth0", nil
}