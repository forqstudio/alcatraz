package vm

import (
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"time"
)

type AgentfsOption func(*AgentfsConfig)

type AgentfsConfig struct {
	Bin        string
	RootfsPath string
	DataDir    string
}

func WithAgentfsBin(bin string) AgentfsOption {
	return func(agentfsConfig *AgentfsConfig) {
		agentfsConfig.Bin = bin
	}
}

func WithRootfsPath(path string) AgentfsOption {
	return func(agentfsConfig *AgentfsConfig) {
		agentfsConfig.RootfsPath = path
	}
}

func WithDataDir(dir string) AgentfsOption {
	return func(agentfsConfig *AgentfsConfig) {
		agentfsConfig.DataDir = dir
	}
}

func NewAgentfsConfig(agentfsOptions ...AgentfsOption) *AgentfsConfig {
	cfg := &AgentfsConfig{}
	for _, opt := range agentfsOptions {
		opt(cfg)
	}
	return cfg
}

type AgentfsInitializer interface {
	Init(
		agentfsBin,
		rootfsPath,
		agentID string) error
}

type AgentfsNFSServer interface {
	Start(
		agentfsBin,
		bindIP string,
		port int,
		agentID string) (NFSProcess, error)
}

type DefaultAgentfsInitializer struct{}

func (DefaultAgentfsInitializer) Init(
	agentfsBin,
	rootfsPath,
	agentID string) error {
	cmd := exec.Command(agentfsBin, "init", "--force", "--base", rootfsPath, agentID)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	return cmd.Run()
}

type NFSProcessCmd struct {
	cmd *exec.Cmd
}

func (n *NFSProcessCmd) GetProcess() interface{} {
	return n.cmd.Process
}

func (n *NFSProcessCmd) Kill() error {
	if n.cmd.Process != nil {
		return n.cmd.Process.Kill()
	}
	return nil
}

func (n *NFSProcessCmd) Wait() error {
	return n.cmd.Wait()
}

func (n *NFSProcessCmd) Start() error {
	return n.cmd.Start()
}

type DefaultAgentfsNFSServer struct{}

func (DefaultAgentfsNFSServer) Start(
	agentfsBin,
	bindIP string,
	port int,
	agentID string) (NFSProcess, error) {
	cmd := exec.Command(agentfsBin, "serve", "nfs", "--bind", bindIP, "--port", fmt.Sprintf("%d", port), agentID)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("failed to start agentfs NFS: %w", err)
	}
	return &NFSProcessCmd{cmd: cmd}, nil
}

type AgentfsService struct {
	initializer AgentfsInitializer
	server      AgentfsNFSServer
}

func NewAgentfsService(opts ...AgentfsOption) *AgentfsService {
	return &AgentfsService{
		initializer: DefaultAgentfsInitializer{},
		server:      DefaultAgentfsNFSServer{},
	}
}

func (agentfsService *AgentfsService) PrepareOverlay(instance VirtualMachineInfo, cfg *AgentfsConfig) error {
	os.MkdirAll(cfg.DataDir, 0755)

	dbPath := filepath.Join(cfg.DataDir, instance.GetAgentID()+".db")
	needsInit := !FileExists(dbPath)

	baseStampFile := filepath.Join(cfg.RootfsPath, "etc/alcatraz-release")
	var currentStamp string
	if FileExists(baseStampFile) {
		cmd := exec.Command("sha256sum", baseStampFile)
		out, err := cmd.Output()
		if err == nil {
			currentStamp = string(out[:64])
		}
	}

	baseStampPath := filepath.Join(cfg.DataDir, instance.GetAgentID()+".base-stamp")

	if FileExists(baseStampPath) && currentStamp != "" {
		existingStamp, err := os.ReadFile(baseStampPath)
		if err == nil && string(existingStamp) != currentStamp+"\n" {
			log.Printf("Rootfs changed for %s, reinitializing", instance.GetAgentID())
			os.Remove(dbPath)
			os.Remove(dbPath + "-wal")
			os.Remove(dbPath + "-shm")
			os.Remove(baseStampPath)
			needsInit = true
		}
	}

	if needsInit {
		log.Printf("Initializing AgentFS overlay for %s", instance.GetAgentID())
		if err := agentfsService.initializer.Init(cfg.Bin, cfg.RootfsPath, instance.GetAgentID()); err != nil {
			return fmt.Errorf("failed to init agentfs: %w", err)
		}
	}

	if currentStamp != "" {
		os.WriteFile(baseStampPath, []byte(currentStamp), 0644)
	}

	return nil
}

func (agentfsService *AgentfsService) StartNFS(
	virtualMachineInfo VirtualMachineInfo,
	agentfsConfig *AgentfsConfig) (NFSProcess, error) {
	log.Printf("Starting AgentFS NFS on %s:%d", virtualMachineInfo.GetHostTapIP(), virtualMachineInfo.GetNFSPort())

	exec.Command("pkill", "-f", fmt.Sprintf("agentfs serve nfs --bind %s --port %d", virtualMachineInfo.GetHostTapIP(), virtualMachineInfo.GetNFSPort())).Run()
	time.Sleep(500 * time.Millisecond)

	proc, err := agentfsService.server.Start(
		agentfsConfig.Bin,
		virtualMachineInfo.GetHostTapIP(),
		virtualMachineInfo.GetNFSPort(),
		virtualMachineInfo.GetAgentID())
	if err != nil {
		return nil, err
	}

	time.Sleep(1 * time.Second)

	if proc.GetProcess() != nil {
		if p, ok := proc.GetProcess().(*os.Process); ok {
			if err := p.Kill(); err == nil {
				proc.Wait()
			}
		}
	}

	return agentfsService.server.Start(
		agentfsConfig.Bin,
		virtualMachineInfo.GetHostTapIP(),
		virtualMachineInfo.GetNFSPort(),
		virtualMachineInfo.GetAgentID())
}

func PrepareAgentfsOverlay(
	instance VirtualMachineInfo,
	agentfsBin,
	rootfsPath,
	agentfsDir string) error {
	os.MkdirAll(agentfsDir, 0755)

	dbPath := filepath.Join(agentfsDir, instance.GetAgentID()+".db")
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

	baseStampPath := filepath.Join(agentfsDir, instance.GetAgentID()+".base-stamp")

	if FileExists(baseStampPath) && currentStamp != "" {
		existingStamp, err := os.ReadFile(baseStampPath)
		if err == nil && string(existingStamp) != currentStamp+"\n" {
			log.Printf("Rootfs changed for %s, reinitializing", instance.GetAgentID())
			os.Remove(dbPath)
			os.Remove(dbPath + "-wal")
			os.Remove(dbPath + "-shm")
			os.Remove(baseStampPath)
			needsInit = true
		}
	}

	if needsInit {
		log.Printf("Initializing AgentFS overlay for %s", instance.GetAgentID())
		cmd := exec.Command(agentfsBin, "init", "--force", "--base", rootfsPath, instance.GetAgentID())
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

func StartAgentfsNFS(
	instanceInfo VirtualMachineInfo,
	agentfsBin string) (NFSProcess, error) {
	log.Printf("Starting AgentFS NFS on %s:%d", instanceInfo.GetHostTapIP(), instanceInfo.GetNFSPort())

	exec.Command("pkill", "-f", fmt.Sprintf("agentfs serve nfs --bind %s --port %d", instanceInfo.GetHostTapIP(), instanceInfo.GetNFSPort())).Run()
	time.Sleep(500 * time.Millisecond)

	cmd := exec.Command(
		agentfsBin,
		"serve",
		"nfs",
		"--bind",
		instanceInfo.GetHostTapIP(),
		"--port",
		fmt.Sprintf("%d", instanceInfo.GetNFSPort()),
		instanceInfo.GetAgentID())
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("failed to start agentfs NFS: %w", err)
	}

	time.Sleep(1 * time.Second)

	if cmd.Process != nil {
		if err := cmd.Process.Kill(); err == nil {
			cmd.Wait()
		}
	}

	cmd = exec.Command(
		agentfsBin,
		"serve",
		"nfs",
		"--bind",
		instanceInfo.GetHostTapIP(),
		"--port",
		fmt.Sprintf("%d", instanceInfo.GetNFSPort()),
		instanceInfo.GetAgentID())
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("failed to start agentfs NFS: %w", err)
	}

	time.Sleep(1 * time.Second)

	return &NFSProcessCmd{cmd: cmd}, nil
}

func CleanupInstance(instance VirtualMachineInfo, maxSlots int) {
	log.Printf("Cleaning up instance %s", instance.GetID())

	if proc := instance.GetNFSProcess(); proc != nil {
		if p, ok := proc.GetProcess().(*os.Process); ok {
			p.Kill()
			proc.Wait()
		}
	}
	exec.Command(
		"pkill",
		"-f",
		fmt.Sprintf("agentfs serve nfs --bind %s --port %d", instance.GetHostTapIP(), instance.GetNFSPort())).Run()

	CleanupTap(instance, maxSlots)

	if FileExists(instance.GetSocket()) {
		os.Remove(instance.GetSocket())
	}
}
