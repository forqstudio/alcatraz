using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Sandboxes.ListSandboxes;

public sealed record ListSandboxesQuery() : IQuery<IReadOnlyList<SandboxResponse>>;
