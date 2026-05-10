using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes.Events;

namespace Alcatraz.Domain.Sandboxes;

public sealed class Sandbox : Entity
{
    private Sandbox(
        Guid id,
        Guid ownerUserId,
        int requestedVcpus,
        int requestedMemoryMib,
        SandboxStatus status,
        DateTime createdOnUtc)
        : base(id)
    {
        OwnerUserId = ownerUserId;
        RequestedVcpus = requestedVcpus;
        RequestedMemoryMib = requestedMemoryMib;
        Status = status;
        CreatedOnUtc = createdOnUtc;
    }

    private Sandbox() { }

    public Guid OwnerUserId { get; private set; }

    public int RequestedVcpus { get; private set; }

    public int RequestedMemoryMib { get; private set; }

    public SandboxStatus Status { get; private set; }

    public DateTime CreatedOnUtc { get; private set; }

    public DateTime? DeletedOnUtc { get; private set; }

    public string? Host { get; private set; }

    public int? Port { get; private set; }

    public int? ActualVcpus { get; private set; }

    public int? ActualMemoryMib { get; private set; }

    public int? BootDurationMs { get; private set; }

    public DateTime? ReadyAtUtc { get; private set; }

    public string? VmmVersion { get; private set; }

    public string? VmmState { get; private set; }

    public int? FirecrackerPid { get; private set; }

    public string? SocketPath { get; private set; }

    public string? TapDevice { get; private set; }

    public string? MacAddress { get; private set; }

    public string? VmIp { get; private set; }

    public string? HostGatewayIp { get; private set; }

    public int? NfsPort { get; private set; }

    public int? WorkerSlotIndex { get; private set; }

    public string? RootfsPath { get; private set; }

    public string? KernelPath { get; private set; }

    public static Sandbox Request(Guid ownerUserId, int vcpus, int memoryMib, DateTime utcNow)
    {
        var sandbox = new Sandbox(
            Guid.NewGuid(),
            ownerUserId,
            vcpus,
            memoryMib,
            SandboxStatus.Provisioning,
            utcNow);

        sandbox.RaiseDomainEvent(new SandboxRequestedDomainEvent(
            sandbox.Id,
            ownerUserId,
            vcpus,
            memoryMib));

        return sandbox;
    }

    public Result MarkRunning(SandboxRuntimeInfo runtime, DateTime utcNow)
    {
        if (Status != SandboxStatus.Provisioning)
        {
            return Result.Failure(SandboxErrors.NotProvisioning);
        }

        Status = SandboxStatus.Running;
        Host = runtime.Host;
        Port = runtime.Port;
        ActualVcpus = runtime.ActualVcpus;
        ActualMemoryMib = runtime.ActualMemoryMib;
        BootDurationMs = runtime.BootDurationMs;
        ReadyAtUtc = runtime.ReadyAtUtc;
        VmmVersion = runtime.VmmVersion;
        VmmState = runtime.VmmState;
        FirecrackerPid = runtime.FirecrackerPid;
        SocketPath = runtime.SocketPath;
        TapDevice = runtime.TapDevice;
        MacAddress = runtime.MacAddress;
        VmIp = runtime.VmIp;
        HostGatewayIp = runtime.HostGatewayIp;
        NfsPort = runtime.NfsPort;
        WorkerSlotIndex = runtime.WorkerSlotIndex;
        RootfsPath = runtime.RootfsPath;
        KernelPath = runtime.KernelPath;

        RaiseDomainEvent(new SandboxBecameRunningDomainEvent(
            Id,
            runtime.Host,
            runtime.Port,
            runtime.ActualVcpus,
            runtime.ActualMemoryMib,
            runtime.BootDurationMs,
            runtime.ReadyAtUtc));

        return Result.Success();
    }

    public Result MarkDeleting(DateTime utcNow)
    {
        if (Status == SandboxStatus.Deleted)
        {
            return Result.Failure(SandboxErrors.AlreadyDeleted);
        }

        if (Status == SandboxStatus.Deleting)
        {
            return Result.Failure(SandboxErrors.AlreadyDeleting);
        }

        Status = SandboxStatus.Deleting;
        DeletedOnUtc = utcNow;

        RaiseDomainEvent(new SandboxDeletionRequestedDomainEvent(Id));

        return Result.Success();
    }

    public Result MarkDestroyed(DateTime utcNow)
    {
        // Worker fires vm.destroyed both when we asked it to (Deleting) and
        // when the VM exited unexpectedly (Provisioning / Running). Map the
        // first to Deleted, the rest to Failed. Already-terminal states are
        // idempotent so at-least-once delivery doesn't bounce.
        switch (Status)
        {
            case SandboxStatus.Deleted:
            case SandboxStatus.Failed:
                return Result.Success();

            case SandboxStatus.Deleting:
                Status = SandboxStatus.Deleted;
                DeletedOnUtc ??= utcNow;
                return Result.Success();

            case SandboxStatus.Provisioning:
            case SandboxStatus.Running:
                Status = SandboxStatus.Failed;
                return Result.Success();

            default:
                return Result.Failure(SandboxErrors.NotFound);
        }
    }

    public Result EnsureOwnedBy(Guid userId) =>
        OwnerUserId == userId
            ? Result.Success()
            : Result.Failure(SandboxErrors.NotFound);

    public bool CanIssueCertificate() => Status == SandboxStatus.Running;
}
