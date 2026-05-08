using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Sandboxes.CreateSandbox;

public sealed record CreateSandboxCommand(int Vcpus, int MemoryMib) : ICommand<SandboxResponse>;
