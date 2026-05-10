namespace Alcatraz.Cli.Commands.Sandboxes;

public sealed record SandboxResponse(
    Guid Id,
    Guid OwnerUserId,
    int Vcpus,
    int MemoryMib,
    int Status,
    DateTime CreatedOnUtc,
    DateTime? DeletedOnUtc,
    string? Host = null,
    int? Port = null,
    int? ActualVcpus = null,
    int? ActualMemoryMib = null,
    int? BootDurationMs = null,
    DateTime? ReadyAtUtc = null,
    string? VmmVersion = null,
    string? VmmState = null,
    int? FirecrackerPid = null,
    string? SocketPath = null,
    string? TapDevice = null,
    string? MacAddress = null,
    string? VmIp = null,
    string? HostGatewayIp = null,
    int? NfsPort = null,
    int? WorkerSlotIndex = null,
    string? RootfsPath = null,
    string? KernelPath = null);

public enum SandboxStatus
{
    Provisioning = 1,
    Running = 2,
    Deleting = 3,
    Deleted = 4,
    Failed = 5,
}
