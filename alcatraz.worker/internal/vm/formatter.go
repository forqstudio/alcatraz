package vm

import "fmt"

type Formatter[T any] interface {
	Format(index int) T
}

type IPFormatter struct{}

func (IPFormatter) Format(index int) string {
	return fmt.Sprintf("172.16.%d.2", index)
}

type HostTapIPFormatter struct{}

func (HostTapIPFormatter) Format(index int) string {
	return fmt.Sprintf("172.16.%d.1", index)
}

type SubnetFormatter struct{}

func (SubnetFormatter) Format(index int) string {
	return fmt.Sprintf("172.16.%d.0/24", index)
}

type TapDevFormatter struct {
	Prefix    string
	MaxSlots  int
}

func (t TapDevFormatter) Format(index int) string {
	return fmt.Sprintf("%s%d", t.Prefix, index)
}

type NFSPortFormatter struct {
	BasePort int
}

func (n NFSPortFormatter) Format(index int) int {
	return n.BasePort + index
}

type SocketFormatter struct {
	AgentfsDirectory string
}

func (s SocketFormatter) Format(agentID string) string {
	return fmt.Sprintf("%s/fc-%s.sock", s.AgentfsDirectory, agentID)
}