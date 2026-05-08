namespace ForqStudio.Application.Sandboxes;

public sealed class SandboxResponse
{
    public Guid Id { get; init; }

    public Guid OwnerUserId { get; init; }

    public int Vcpus { get; init; }

    public int MemoryMib { get; init; }

    public int Status { get; init; }

    public DateTime CreatedOnUtc { get; init; }

    public DateTime? DeletedOnUtc { get; init; }
}
