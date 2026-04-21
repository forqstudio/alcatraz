package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"

	firecracker "github.com/firecracker-microvm/firecracker-go-sdk"
	"github.com/firecracker-microvm/firecracker-go-sdk/client/models"
)

const (
	firecrackerBin   = "./bin/firecracker-v1.15.1"
	kernelPath       = "./linux-amazon/vmlinux"
	rootfsPath       = "./rootfs"
	agentfsDir       = ".agentfs"
	agentID          = "firecracker-dev"

	tapDev           = "fc-tap0"
	hostTapIP        = "172.16.0.1"
	vmIP             = "172.16.0.2"
	vmSubnet         = "172.16.0.0/24"
	vmSubnetMask     = "255.255.255.0"
	vmHostname       = "alcatraz"
	nfsPort          = 11111

	vmVCPUs          = 4
	vmMemMib         = 8192

	guestMAC         = "AA:FC:00:00:00:01"
)

var (
	agentfsBin string
	vmConfigPath string
)

func runCmd(name string, args ...string) error {
	cmd := exec.Command(name, args...)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	return cmd.Run()
}

func runCmdWithEnv(env []string, name string, args ...string) error {
	cmd := exec.Command(name, args...)
	cmd.Env = append(os.Environ(), env...)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	return cmd.Run()
}

func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func setupTap() error {
	runCmd("ip", "link", "del", tapDev)
	if err := runCmd("ip", "tuntap", "add", "dev", tapDev, "mode", "tap"); err != nil {
		return fmt.Errorf("failed to create tap device: %w", err)
	}
	if err := runCmd("ip", "addr", "add", fmt.Sprintf("%s/24", hostTapIP), "dev", tapDev); err != nil {
		return fmt.Errorf("failed to assign IP to tap: %w", err)
	}
	if err := runCmd("ip", "link", "set", tapDev, "up"); err != nil {
		return fmt.Errorf("failed to bring up tap: %w", err)
	}
	return nil
}

func setupNAT() error {
	if err := runCmd("sysctl", "-w", "net.ipv4.ip_forward=1"); err != nil {
		return fmt.Errorf("failed to enable IP forward: %w", err)
	}
	if err := runCmd("sysctl", "-w", fmt.Sprintf("net.ipv4.conf.%s.route_localnet=1", tapDev)); err != nil {
		return fmt.Errorf("failed to enable route_localnet: %w", err)
	}

	if err := runCmd("iptables", "-t", "nat", "-A", "POSTROUTING", "-s", vmSubnet, "-o", "eth0", "-j", "MASQUERADE"); err != nil {
		return fmt.Errorf("failed to add NAT rule: %w", err)
	}
	if err := runCmd("iptables", "-A", "FORWARD", "-i", "eth0", "-o", tapDev, "-m", "state", "--state", "RELATED,ESTABLISHED", "-j", "ACCEPT"); err != nil {
		return fmt.Errorf("failed to add forward rule 1: %w", err)
	}
	if err := runCmd("iptables", "-A", "FORWARD", "-i", tapDev, "-o", "eth0", "-j", "ACCEPT"); err != nil {
		return fmt.Errorf("failed to add forward rule 2: %w", err)
	}

	return nil
}

func findAgentfsBin() (string, error) {
	if agentfsBin != "" {
		return agentfsBin, nil
	}

	// Check common locations
	locations := []string{
		"agentfs",
		"/home/dev/.cargo/bin/agentfs",
		"/usr/local/bin/agentfs",
	}

	for _, loc := range locations {
		if path, err := exec.LookPath(loc); err == nil {
			return path, nil
		}
	}

	return "", fmt.Errorf("agentfs not found in PATH or common locations")
}

func resolveAgentfsBin() error {
	bin, err := findAgentfsBin()
	if err != nil {
		return err
	}
	agentfsBin = bin
	log.Printf("Using agentfs: %s", agentfsBin)
	return nil
}

func prepareAgentfsOverlay() error {
	if err := os.MkdirAll(agentfsDir, 0755); err != nil {
		return fmt.Errorf("failed to create agentfs dir: %w", err)
	}

	dbPath := filepath.Join(agentfsDir, agentID+".db")

	// Check if we need to initialize the overlay
	needsInit := !fileExists(dbPath)

	// Compute base stamp
	baseStampFile := filepath.Join(rootfsPath, "etc/alcatraz-release")
	var currentStamp string
	if fileExists(baseStampFile) {
		cmd := exec.Command("sha256sum", baseStampFile)
		out, err := cmd.Output()
		if err == nil {
			currentStamp = string(out[:64])
		}
	}

	baseStampPath := filepath.Join(agentfsDir, agentID+".base-stamp")

	// Check if rootfs changed
	if fileExists(baseStampPath) && currentStamp != "" {
		existingStamp, err := os.ReadFile(baseStampPath)
		if err == nil && string(existingStamp) != currentStamp+"\n" {
			log.Println("Rootfs changed, reinitializing AgentFS overlay")
			os.Remove(dbPath)
			os.Remove(dbPath + "-wal")
			os.Remove(dbPath + "-shm")
			os.Remove(baseStampPath)
			needsInit = true
		}
	}

	if needsInit {
		log.Printf("Initializing AgentFS overlay for %s", agentID)
		cmd := exec.Command(agentfsBin, "init", "--force", "--base", rootfsPath, agentID)
		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr
		if err := cmd.Run(); err != nil {
			return fmt.Errorf("failed to init agentfs: %w", err)
		}
	}

	// Save base stamp
	if currentStamp != "" {
		if err := os.WriteFile(baseStampPath, []byte(currentStamp), 0644); err != nil {
			return fmt.Errorf("failed to write base stamp: %w", err)
		}
	}

	return nil
}

var agentfsNFSProc *exec.Cmd

func startAgentfsNFS() error {
	log.Printf("Starting AgentFS NFS server on %s:%d", hostTapIP, nfsPort)

	// Kill any stale agentfs NFS servers
	exec.Command("pkill", "-f", fmt.Sprintf("agentfs serve nfs --bind %s --port %d", hostTapIP, nfsPort)).Run()
	time.Sleep(500 * time.Millisecond)

	cmd := exec.Command(agentfsBin, "serve", "nfs", "--bind", hostTapIP, "--port", fmt.Sprintf("%d", nfsPort), agentID)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("failed to start agentfs NFS: %w", err)
	}
	agentfsNFSProc = cmd

	time.Sleep(1 * time.Second)

	if agentfsNFSProc != nil && agentfsNFSProc.Process != nil {
		if err := agentfsNFSProc.Process.Kill(); err == nil {
			agentfsNFSProc.Wait()
		}
	}

	// Re-start properly
	cmd = exec.Command(agentfsBin, "serve", "nfs", "--bind", hostTapIP, "--port", fmt.Sprintf("%d", nfsPort), agentID)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("failed to start agentfs NFS: %w", err)
	}
	agentfsNFSProc = cmd

	time.Sleep(1 * time.Second)

	return nil
}

func stopAgentfsNFS() {
	if agentfsNFSProc != nil && agentfsNFSProc.Process != nil {
		agentfsNFSProc.Process.Kill()
		agentfsNFSProc.Wait()
	}
	// Also kill any remaining
	exec.Command("pkill", "-f", fmt.Sprintf("agentfs serve nfs --bind %s --port %d", hostTapIP, nfsPort)).Run()
}

func createVMConfig() (string, error) {
	vmConfigPath = filepath.Join(".", fmt.Sprintf("vm_config.%s.json", agentID))

	bootArgs := fmt.Sprintf(
		"console=ttyS0 reboot=k panic=1 pci=off loglevel=7 printk.devkmsg=on ip=%s::%s:%s:%s:eth0:off root=/dev/nfs nfsroot=%s:/,nfsvers=3,tcp,nolock,port=%d,mountport=%d rw init=/init",
		vmIP, hostTapIP, vmSubnetMask, vmHostname, hostTapIP, nfsPort, nfsPort,
	)

	config := map[string]interface{}{
		"boot-source": map[string]interface{}{
			"kernel_image_path": kernelPath,
			"boot_args":         bootArgs,
		},
		"drives":              []interface{}{},
		"network-interfaces": []map[string]interface{}{
			{
				"iface_id":       "eth0",
				"guest_mac":      guestMAC,
				"host_dev_name":  tapDev,
			},
		},
		"machine-config": map[string]interface{}{
			"vcpu_count":  vmVCPUs,
			"mem_size_mib": vmMemMib,
		},
	}

	data, err := json.MarshalIndent(config, "", "  ")
	if err != nil {
		return "", fmt.Errorf("failed to marshal config: %w", err)
	}

	if err := os.WriteFile(vmConfigPath, data, 0644); err != nil {
		return "", fmt.Errorf("failed to write config: %w", err)
	}

	return vmConfigPath, nil
}

func getHostIface() (string, error) {
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

func cleanup() {
	log.Println("Cleaning up...")

	stopAgentfsNFS()

	runCmd("ip", "link", "del", tapDev)

	hostIface, err := getHostIface()
	if err == nil {
		runCmd("iptables", "-t", "nat", "-D", "POSTROUTING", "-s", vmSubnet, "-o", hostIface, "-j", "MASQUERADE")
		runCmd("iptables", "-D", "FORWARD", "-i", hostIface, "-o", tapDev, "-m", "state", "--state", "RELATED,ESTABLISHED", "-j", "ACCEPT")
		runCmd("iptables", "-D", "FORWARD", "-i", tapDev, "-o", hostIface, "-j", "ACCEPT")
	}

	if vmConfigPath != "" {
		os.Remove(vmConfigPath)
	}
}

func main() {
	ctx := context.Background()

	if os.Getuid() != 0 {
		log.Fatal("This program must be run as root")
	}

	if !fileExists(firecrackerBin) {
		log.Fatalf("Firecracker binary not found at %s", firecrackerBin)
	}
	if !fileExists(kernelPath) {
		log.Fatalf("Kernel not found at %s", kernelPath)
	}
	if !fileExists(rootfsPath) {
		log.Fatalf("Rootfs not found at %s", rootfsPath)
	}

	if err := resolveAgentfsBin(); err != nil {
		log.Fatal(err)
	}

	log.Println("Setting up TAP device...")
	if err := setupTap(); err != nil {
		log.Fatal(err)
	}

	log.Println("Setting up NAT...")
	if err := setupNAT(); err != nil {
		log.Fatal(err)
	}

	log.Println("Preparing AgentFS overlay...")
	if err := prepareAgentfsOverlay(); err != nil {
		log.Fatal(err)
	}

	log.Println("Starting AgentFS NFS server...")
	if err := startAgentfsNFS(); err != nil {
		log.Fatal(err)
	}

	log.Println("Creating VM config...")
	if _, err := createVMConfig(); err != nil {
		log.Fatal(err)
	}

	socketPath := filepath.Join(agentfsDir, "firecracker.sock")

	cfg := firecracker.Config{
		SocketPath:      socketPath,
		KernelImagePath: kernelPath,
		KernelArgs:      fmt.Sprintf("console=ttyS0 reboot=k panic=1 pci=off loglevel=7 printk.devkmsg=on ip=%s::%s:%s:%s:eth0:off root=/dev/nfs nfsroot=%s:/,nfsvers=3,tcp,nolock,port=%d,mountport=%d rw init=/init",
			vmIP, hostTapIP, vmSubnetMask, vmHostname, hostTapIP, nfsPort, nfsPort),
		MachineCfg: models.MachineConfiguration{
			VcpuCount:  firecracker.Int64(int64(vmVCPUs)),
			MemSizeMib: firecracker.Int64(int64(vmMemMib)),
		},
		NetworkInterfaces: []firecracker.NetworkInterface{
			{
				StaticConfiguration: &firecracker.StaticNetworkConfiguration{
					MacAddress:  guestMAC,
					HostDevName: tapDev,
				},
			},
		},
	}

	cmd := firecracker.VMCommandBuilder{}.
		WithBin(firecrackerBin).
		WithSocketPath(socketPath).
		Build(ctx)

	m, err := firecracker.NewMachine(ctx, cfg, firecracker.WithProcessRunner(cmd))
	if err != nil {
		log.Fatalf("Failed to create machine: %v", err)
	}

	log.Printf("Starting Firecracker ( %d vCPU, %d MiB)", vmVCPUs, vmMemMib)
	if err := m.Start(ctx); err != nil {
		log.Fatalf("Failed to start machine: %v", err)
	}

	log.Println("VM started. Waiting for exit...")

	if err := m.Wait(ctx); err != nil {
		log.Printf("Wait returned: %v", err)
	}

	log.Println("VM exited")
	cleanup()
}