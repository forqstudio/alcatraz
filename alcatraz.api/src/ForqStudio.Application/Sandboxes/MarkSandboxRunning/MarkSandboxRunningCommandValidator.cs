using FluentValidation;

namespace ForqStudio.Application.Sandboxes.MarkSandboxRunning;

internal sealed class MarkSandboxRunningCommandValidator : AbstractValidator<MarkSandboxRunningCommand>
{
    public MarkSandboxRunningCommandValidator()
    {
        RuleFor(c => c.SandboxId).NotEmpty();

        RuleFor(c => c.Host).NotEmpty().MaximumLength(255);

        RuleFor(c => c.Port).InclusiveBetween(1, 65535);
    }
}
