using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Domain.Sandboxes.Usage;

public static class SandboxUsageErrors
{
    public static Error AlreadyRecorded = new(
        "SandboxUsage.AlreadyRecorded",
        "A usage record already exists for this sandbox");

    public static Error SandboxNotFinalisable = new(
        "SandboxUsage.SandboxNotFinalisable",
        "Sandbox cannot be finalised because it never reached the Running state");

    public static Error SandboxNotFound = new(
        "SandboxUsage.SandboxNotFound",
        "Sandbox with the specified identifier was not found");

    public static Error NotFound = new(
        "SandboxUsage.NotFound",
        "No usage record was found for the specified sandbox");
}
