namespace Alcatraz.Application.Sandboxes;

public sealed class SandboxResponse
{
    public Guid Id { get; init; }

    public Guid OwnerUserId { get; init; }

    public int Vcpus { get; init; }

    public int MemoryMib { get; init; }

    public int Status { get; init; }

    public DateTime CreatedOnUtc { get; init; }

    public DateTime? DeletedOnUtc { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    public int? ActualVcpus { get; init; }

    public int? ActualMemoryMib { get; init; }

    public int? BootDurationMs { get; init; }

    public DateTime? ReadyAtUtc { get; init; }

    public string? VmmVersion { get; init; }

    public string? VmmState { get; init; }

    public int? FirecrackerPid { get; init; }

    public string? SocketPath { get; init; }

    public string? TapDevice { get; init; }

    public string? MacAddress { get; init; }

    public string? VmIp { get; init; }

    public string? HostGatewayIp { get; init; }

    public int? NfsPort { get; init; }

    public int? WorkerSlotIndex { get; init; }

    public string? RootfsPath { get; init; }

    public string? KernelPath { get; init; }
}
