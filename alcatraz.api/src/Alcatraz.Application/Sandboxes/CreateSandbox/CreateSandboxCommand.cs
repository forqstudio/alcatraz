using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Sandboxes.CreateSandbox;

public sealed record CreateSandboxCommand(int Vcpus, int MemoryMib) : ICommand<SandboxResponse>;
