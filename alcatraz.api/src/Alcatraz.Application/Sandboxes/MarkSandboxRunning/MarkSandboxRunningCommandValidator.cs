using FluentValidation;

namespace Alcatraz.Application.Sandboxes.MarkSandboxRunning;

internal sealed class MarkSandboxRunningCommandValidator : AbstractValidator<MarkSandboxRunningCommand>
{
    public MarkSandboxRunningCommandValidator()
    {
        RuleFor(c => c.SandboxId).NotEmpty();

        RuleFor(c => c.Runtime).NotNull();

        RuleFor(c => c.Runtime.Host).NotEmpty().MaximumLength(255);

        RuleFor(c => c.Runtime.Port).InclusiveBetween(1, 65535);

        RuleFor(c => c.Runtime.ActualVcpus).GreaterThan(0);

        RuleFor(c => c.Runtime.ActualMemoryMib).GreaterThan(0);

        RuleFor(c => c.Runtime.BootDurationMs).GreaterThanOrEqualTo(0);
    }
}
