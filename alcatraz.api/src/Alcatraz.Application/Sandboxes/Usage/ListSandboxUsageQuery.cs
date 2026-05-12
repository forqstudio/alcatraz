using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Sandboxes.Usage;

public sealed record ListSandboxUsageQuery : IQuery<IReadOnlyList<SandboxUsageResponse>>;
