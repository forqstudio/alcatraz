using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Sandboxes.GetSandbox;

public sealed record GetSandboxQuery(Guid SandboxId) : IQuery<SandboxResponse>;
