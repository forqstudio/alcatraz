using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Sandboxes.MarkSandboxRunning;

public sealed record MarkSandboxRunningCommand(Guid SandboxId, string Host, int Port) : ICommand;
