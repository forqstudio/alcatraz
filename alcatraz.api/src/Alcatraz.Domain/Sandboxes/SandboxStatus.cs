namespace Alcatraz.Domain.Sandboxes;

public enum SandboxStatus
{
    Provisioning = 1,
    Running = 2,
    Deleting = 3,
    Deleted = 4,
    Failed = 5,
}
