using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Sandboxes.Usage;

public sealed record GetSandboxUsageQuery(Guid SandboxId) : IQuery<SandboxUsageResponse>;
