using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Sandboxes.Usage;

namespace Alcatraz.Application.Sandboxes.MarkSandboxUsageRecorded;

public sealed record MarkSandboxUsageRecordedCommand(Guid SandboxId, SandboxUsageFinal Final) : ICommand;
