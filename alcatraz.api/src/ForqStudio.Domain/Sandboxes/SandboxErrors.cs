using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Domain.Sandboxes;

public static class SandboxErrors
{
    public static Error NotFound = new(
        "Sandbox.NotFound",
        "Sandbox with the specified identifier was not found");

    public static Error AlreadyDeleting = new(
        "Sandbox.AlreadyDeleting",
        "Sandbox is already being deleted");

    public static Error AlreadyDeleted = new(
        "Sandbox.AlreadyDeleted",
        "Sandbox has already been deleted");

    public static Error InvalidStateForCertIssue = new(
        "Sandbox.InvalidStateForCertIssue",
        "Sandbox is not in a state that allows certificate issuance");

    public static Error NotProvisioning = new(
        "Sandbox.NotProvisioning",
        "Sandbox cannot be marked Running because it is not in the Provisioning state");

    public static Error NotReady = new(
        "Sandbox.NotReady",
        "Sandbox is not yet ready to accept connections");
}
