using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Sandboxes.MarkSandboxRunning;

public sealed record MarkSandboxRunningCommand(Guid SandboxId, string Host, int Port) : ICommand;
