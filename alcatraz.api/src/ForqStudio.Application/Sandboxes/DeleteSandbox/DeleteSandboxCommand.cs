using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Sandboxes.DeleteSandbox;

public sealed record DeleteSandboxCommand(Guid SandboxId) : ICommand;
