package vm

import (
	"context"
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"

	firecracker "github.com/firecracker-microvm/firecracker-go-sdk"
	"github.com/firecracker-microvm/firecracker-go-sdk/client/models"
	"github.com/google/uuid"

	"firecracker-agentfs/internal/config"
)

type InstanceManager struct {
	mu       sync.Mutex
	instances map[string]*Instance
	pool      []int
	maxVMs   int
}

type Instance struct {
	ID          string
	VCPUs      int
	MemoryMib  int
	KernelArgs string
	Index     int

	TapDev    string
	HostTapIP string
	VMIP     string
	Subnet   string
	NFSPort  int
	Socket   string
	AgentID  string

	Machine   *firecracker.Machine
	NFSProc   *exec.Cmd
	Config   firecracker.Config
	MachineCfg *models.MachineConfiguration

	mu sync.Mutex
}

func NewInstanceManager(maxVMs int) *InstanceManager {
	pool := make([]int, maxVMs)
	for i := range pool {
		pool[i] = i
	}
	return &InstanceManager{
		instances: make(map[string]*Instance),
		pool:      pool,
		maxVMs:   maxVMs,
	}
}

func (m *InstanceManager) Allocate() (int, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if len(m.pool) == 0 {
		return 0, fmt.Errorf("no available VM slots (max %d)", m.maxVMs)
	}
	idx := m.pool[0]
	m.pool = m.pool[1:]
	return idx, nil
}

func (m *InstanceManager) Release(idx int) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.pool = append(m.pool, idx)
}

func (m *InstanceManager) AddInstance(inst *Instance) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.instances[inst.ID] = inst
}

func (m *InstanceManager) RemoveInstance(id string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.instances, id)
}

func (m *InstanceManager) GetInstance(id string) *Instance {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.instances[id]
}

func (m *InstanceManager) ListInstances() []*Instance {
	m.mu.Lock()
	defer m.mu.Unlock()
	instances := make([]*Instance, 0, len(m.instances))
	for _, inst := range m.instances {
		instances = append(instances, inst)
	}
	return instances
}

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

func FindAgentfsBin(customPath string) (string, error) {
	if customPath != "" {
		return customPath, nil
	}
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
	return "", fmt.Errorf("agentfs not found")
}

func ResolveFirecrackerBin(customPath string) (string, error) {
	if customPath != "" && FileExists(customPath) {
		return customPath, nil
	}
	if FileExists(config.FirecrackerBin) {
		return config.FirecrackerBin, nil
	}
	if path, err := exec.LookPath("firecracker"); err == nil {
		return path, nil
	}
	return "", fmt.Errorf("firecracker binary not found")
}

func SetupTap(instance *Instance) error {
	if err := RunCmd("ip", "link", "del", instance.TapDev); err != nil {
		log.Printf("warning: could not delete tap device: %v", err)
	}
	if err := RunCmd("ip", "tuntap", "add", "dev", instance.TapDev, "mode", "tap"); err != nil {
		return fmt.Errorf("failed to create tap device: %w", err)
	}
	if err := RunCmd("ip", "addr", "add", fmt.Sprintf("%s/24", instance.HostTapIP), "dev", instance.TapDev); err != nil {
		return fmt.Errorf("failed to assign IP to tap: %w", err)
	}
	if err := RunCmd("ip", "link", "set", instance.TapDev, "up"); err != nil {
		return fmt.Errorf("failed to bring up tap: %w", err)
	}
	return nil
}

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

func SetupNAT(instance *Instance) error {
	if err := RunCmd("sysctl", "-w", "net.ipv4.ip_forward=1"); err != nil {
		return fmt.Errorf("failed to enable IP forward: %w", err)
	}
	if err := RunCmd("sysctl", "-w", fmt.Sprintf("net.ipv4.conf.%s.route_localnet=1", instance.TapDev)); err != nil {
		return fmt.Errorf("failed to enable route_localnet: %w", err)
	}

	hostIface, err := GetHostIface()
	if err != nil {
		hostIface = "eth0"
	}

	if err := RunCmd("iptables", "-t", "nat", "-A", "POSTROUTING", "-s", instance.Subnet, "-o", hostIface, "-j", "MASQUERADE"); err != nil {
		return fmt.Errorf("failed to add NAT rule: %w", err)
	}
	if err := RunCmd("iptables", "-A", "FORWARD", "-i", hostIface, "-o", instance.TapDev, "-m", "state", "--state", "RELATED,ESTABLISHED", "-j", "ACCEPT"); err != nil {
		return fmt.Errorf("failed to add forward rule 1: %w", err)
	}
	if err := RunCmd("iptables", "-A", "FORWARD", "-i", instance.TapDev, "-o", hostIface, "-j", "ACCEPT"); err != nil {
		return fmt.Errorf("failed to add forward rule 2: %w", err)
	}

	return nil
}

func CleanupNAT(instance *Instance) {
	hostIface, _ := GetHostIface()
	RunCmd("iptables", "-t", "nat", "-D", "POSTROUTING", "-s", instance.Subnet, "-o", hostIface, "-j", "MASQUERADE")
	RunCmd("iptables", "-D", "FORWARD", "-i", hostIface, "-o", instance.TapDev, "-m", "state", "--state", "RELATED,ESTABLISHED", "-j", "ACCEPT")
	RunCmd("iptables", "-D", "FORWARD", "-i", instance.TapDev, "-o", hostIface, "-j", "ACCEPT")
}

func PrepareAgentfsOverlay(instance *Instance, agentfsBin, rootfsPath, agentfsDir string) error {
	os.MkdirAll(agentfsDir, 0755)

	dbPath := filepath.Join(agentfsDir, instance.AgentID+".db")
	needsInit := !FileExists(dbPath)

	baseStampFile := filepath.Join(rootfsPath, "etc/alcatraz-release")
	var currentStamp string
	if FileExists(baseStampFile) {
		cmd := exec.Command("sha256sum", baseStampFile)
		out, err := cmd.Output()
		if err == nil {
			currentStamp = string(out[:64])
		}
	}

	baseStampPath := filepath.Join(agentfsDir, instance.AgentID+".base-stamp")

	if FileExists(baseStampPath) && currentStamp != "" {
		existingStamp, err := os.ReadFile(baseStampPath)
		if err == nil && string(existingStamp) != currentStamp+"\n" {
			log.Printf("Rootfs changed for %s, reinitializing", instance.AgentID)
			os.Remove(dbPath)
			os.Remove(dbPath + "-wal")
			os.Remove(dbPath + "-shm")
			os.Remove(baseStampPath)
			needsInit = true
		}
	}

	if needsInit {
		log.Printf("Initializing AgentFS overlay for %s", instance.AgentID)
		cmd := exec.Command(agentfsBin, "init", "--force", "--base", rootfsPath, instance.AgentID)
		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr
		if err := cmd.Run(); err != nil {
			return fmt.Errorf("failed to init agentfs: %w", err)
		}
	}

	if currentStamp != "" {
		os.WriteFile(baseStampPath, []byte(currentStamp), 0644)
	}

	return nil
}

func StartAgentfsNFS(instance *Instance, agentfsBin string) error {
	log.Printf("Starting AgentFS NFS on %s:%d", instance.HostTapIP, instance.NFSPort)

	exec.Command("pkill", "-f", fmt.Sprintf("agentfs serve nfs --bind %s --port %d", instance.HostTapIP, instance.NFSPort)).Run()
	time.Sleep(500 * time.Millisecond)

	cmd := exec.Command(agentfsBin, "serve", "nfs", "--bind", instance.HostTapIP, "--port", fmt.Sprintf("%d", instance.NFSPort), instance.AgentID)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("failed to start agentfs NFS: %w", err)
	}
	instance.NFSProc = cmd

	time.Sleep(1 * time.Second)

	if instance.NFSProc != nil && instance.NFSProc.Process != nil {
		if err := instance.NFSProc.Process.Kill(); err == nil {
			instance.NFSProc.Wait()
		}
	}

	cmd = exec.Command(agentfsBin, "serve", "nfs", "--bind", instance.HostTapIP, "--port", fmt.Sprintf("%d", instance.NFSPort), instance.AgentID)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("failed to start agentfs NFS: %w", err)
	}
	instance.NFSProc = cmd

	time.Sleep(1 * time.Second)

	return nil
}

func CleanupInstance(instance *Instance) {
	log.Printf("Cleaning up instance %s", instance.ID)

	if instance.NFSProc != nil && instance.NFSProc.Process != nil {
		instance.NFSProc.Process.Kill()
		instance.NFSProc.Wait()
	}
	exec.Command("pkill", "-f", fmt.Sprintf("agentfs serve nfs --bind %s --port %d", instance.HostTapIP, instance.NFSPort)).Run()

	RunCmd("ip", "link", "del", instance.TapDev)

	CleanupNAT(instance)

	if FileExists(instance.Socket) {
		os.Remove(instance.Socket)
	}
}

type SpawnOptions struct {
	AgentfsBin   string
	FirecrackerBin string
	Rootfs       string
	Kernel       string
	AgentfsDir   string
}

func FormatHostTapIP(idx int) string {
	return fmt.Sprintf("172.16.%d.1", idx)
}

func FormatVMIP(idx int) string {
	return fmt.Sprintf("172.16.%d.2", idx)
}

func FormatSubnet(idx int) string {
	return fmt.Sprintf("172.16.%d.0/24", idx)
}

func FormatNFSPort(idx int) int {
	return config.BaseNFSPort + idx
}

func FormatTapDev(idx int) string {
	return fmt.Sprintf("%s%d", config.BaseTapDev, idx)
}

func FormatSocket(agentfsDir, vmID string) string {
	return filepath.Join(agentfsDir, fmt.Sprintf("fc-%s.sock", vmID))
}

func Spawn(ctx context.Context, mgr *InstanceManager, req *config.VMRequest, opts *SpawnOptions) (*Instance, error) {
	idx, err := mgr.Allocate()
	if err != nil {
		return nil, err
	}

	vmID := req.ID
	if vmID == "" {
		vmID = uuid.New().String()
	}

	vcpus := req.VCPUs
	if vcpus <= 0 {
		vcpus = config.DefaultVCPUs
	}

	memMib := req.MemoryMib
	if memMib <= 0 {
		memMib = config.DefaultMemMib
	}

	kernelArgs := req.KernelArgs
	if kernelArgs == "" {
		kernelArgs = config.DefaultKernelArgs
	}

	hostTapIP := FormatHostTapIP(idx)
	vmIP := FormatVMIP(idx)
	subnet := FormatSubnet(idx)
	nfsPort := FormatNFSPort(idx)
	tapDev := FormatTapDev(idx)
	socket := FormatSocket(opts.AgentfsDir, vmID)
	agentID := vmID

	instance := &Instance{
		ID:          vmID,
		VCPUs:       vcpus,
		MemoryMib:  memMib,
		KernelArgs: kernelArgs,
		Index:      idx,

		TapDev:    tapDev,
		HostTapIP: hostTapIP,
		VMIP:      vmIP,
		Subnet:    subnet,
		NFSPort:   nfsPort,
		Socket:    socket,
		AgentID:   agentID,
	}

	log.Printf("Spawning VM %s (vCPUs: %d, Mem: %d MiB, idx: %d)", vmID, vcpus, memMib, idx)

	if err := SetupTap(instance); err != nil {
		mgr.Release(idx)
		return nil, fmt.Errorf("setup tap: %w", err)
	}

	if err := SetupNAT(instance); err != nil {
		CleanupInstance(instance)
		mgr.Release(idx)
		return nil, fmt.Errorf("setup nat: %w", err)
	}

	if err := PrepareAgentfsOverlay(instance, opts.AgentfsBin, opts.Rootfs, opts.AgentfsDir); err != nil {
		CleanupInstance(instance)
		mgr.Release(idx)
		return nil, fmt.Errorf("prepare agentfs: %w", err)
	}

	if err := StartAgentfsNFS(instance, opts.AgentfsBin); err != nil {
		CleanupInstance(instance)
		mgr.Release(idx)
		return nil, fmt.Errorf("start agentfs nfs: %w", err)
	}

	subnetMask := "255.255.255.0"
	bootArgs := fmt.Sprintf(
		"console=ttyS0 reboot=k panic=1 pci=off %s ip=%s::%s:%s:%s:eth0:off root=/dev/nfs nfsroot=%s:/,nfsvers=3,tcp,nolock,port=%d,mountport=%d rw init=/init",
		kernelArgs, vmIP, hostTapIP, subnetMask, config.VMHostname, hostTapIP, nfsPort, nfsPort,
	)

	cfg := firecracker.Config{
		SocketPath:      instance.Socket,
		KernelImagePath: opts.Kernel,
		KernelArgs:      bootArgs,
		MachineCfg: models.MachineConfiguration{
			VcpuCount:  firecracker.Int64(int64(vcpus)),
			MemSizeMib: firecracker.Int64(int64(memMib)),
		},
		NetworkInterfaces: []firecracker.NetworkInterface{
			{
				StaticConfiguration: &firecracker.StaticNetworkConfiguration{
					MacAddress:  config.GuestMAC,
					HostDevName: tapDev,
				},
			},
		},
	}

	fcBinPath, err := ResolveFirecrackerBin(opts.FirecrackerBin)
	if err != nil {
		CleanupInstance(instance)
		mgr.Release(idx)
		return nil, err
	}

	cmd := firecracker.VMCommandBuilder{}.
		WithBin(fcBinPath).
		WithSocketPath(instance.Socket).
		Build(ctx)

	m, err := firecracker.NewMachine(ctx, cfg, firecracker.WithProcessRunner(cmd))
	if err != nil {
		CleanupInstance(instance)
		mgr.Release(idx)
		return nil, fmt.Errorf("create machine: %w", err)
	}

	instance.Machine = m
	instance.Config = cfg

	mgr.AddInstance(instance)

	if err := m.Start(ctx); err != nil {
		mgr.RemoveInstance(vmID)
		CleanupInstance(instance)
		mgr.Release(idx)
		return nil, fmt.Errorf("start machine: %w", err)
	}

	log.Printf("VM %s started (IP: %s)", vmID, vmIP)

	go func() {
		id := instance.ID
		if err := m.Wait(ctx); err != nil {
			log.Printf("VM %s wait error: %v", id, err)
		}
		log.Printf("VM %s exited", id)
		mgr.RemoveInstance(id)
		CleanupInstance(instance)
		mgr.Release(idx)
	}()

	return instance, nil
}

func StopVM(inst *Instance) error {
	if inst.Machine != nil {
		return inst.Machine.StopVMM()
	}
	return nil
}