namespace Alcatraz.Cli.Commands.Sandboxes;

public sealed record SandboxResponse(
    Guid Id,
    Guid OwnerUserId,
    int Vcpus,
    int MemoryMib,
    int Status,
    DateTime CreatedOnUtc,
    DateTime? DeletedOnUtc);

public enum SandboxStatus
{
    Provisioning = 1,
    Running = 2,
    Deleting = 3,
    Deleted = 4,
    Failed = 5,
}
