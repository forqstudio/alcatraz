using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Sandboxes.Events;

namespace ForqStudio.Domain.Sandboxes;

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

    public Result EnsureOwnedBy(Guid userId) =>
        OwnerUserId == userId
            ? Result.Success()
            : Result.Failure(SandboxErrors.NotFound);

    public bool CanIssueCertificate() =>
        Status is SandboxStatus.Provisioning or SandboxStatus.Running;
}
