using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Sandboxes.DeleteSandbox;

public sealed record DeleteSandboxCommand(Guid SandboxId) : ICommand;
