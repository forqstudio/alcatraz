using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Sandboxes.MarkSandboxDestroyed;

public sealed record MarkSandboxDestroyedCommand(Guid SandboxId) : ICommand;
