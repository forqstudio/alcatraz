namespace Alcatraz.Domain.Sandboxes;

// Snapshot of what the worker observed about a microVM at the moment it became
// reachable. Group A fields are always present (the worker can answer them
// from its own memory). Group B fields may be null when the Firecracker
// metadata API failed — the VM is still running, we just couldn't read the
// extra detail.
public sealed record SandboxRuntimeInfo(
    string Host,
    int Port,
    int ActualVcpus,
    int ActualMemoryMib,
    int BootDurationMs,
    DateTime ReadyAtUtc,
    string? VmmVersion,
    string? VmmState,
    int? FirecrackerPid,
    string SocketPath,
    string TapDevice,
    string MacAddress,
    string VmIp,
    string HostGatewayIp,
    int NfsPort,
    int WorkerSlotIndex,
    string RootfsPath,
    string KernelPath);
