using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Sandboxes.ListSandboxes;

public sealed record ListSandboxesQuery() : IQuery<IReadOnlyList<SandboxResponse>>;
