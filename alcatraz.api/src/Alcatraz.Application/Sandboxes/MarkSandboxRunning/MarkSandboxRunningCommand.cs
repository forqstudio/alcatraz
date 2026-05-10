using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Sandboxes;

namespace Alcatraz.Application.Sandboxes.MarkSandboxRunning;

public sealed record MarkSandboxRunningCommand(Guid SandboxId, SandboxRuntimeInfo Runtime) : ICommand;
