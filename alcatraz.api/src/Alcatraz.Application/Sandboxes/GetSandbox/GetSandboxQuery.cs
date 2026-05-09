using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Sandboxes.GetSandbox;

public sealed record GetSandboxQuery(Guid SandboxId) : IQuery<SandboxResponse>;
