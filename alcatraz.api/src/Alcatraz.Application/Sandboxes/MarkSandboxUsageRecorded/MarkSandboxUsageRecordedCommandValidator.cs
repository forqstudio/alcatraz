using FluentValidation;

namespace Alcatraz.Application.Sandboxes.MarkSandboxUsageRecorded;

internal sealed class MarkSandboxUsageRecordedCommandValidator
    : AbstractValidator<MarkSandboxUsageRecordedCommand>
{
    public MarkSandboxUsageRecordedCommandValidator()
    {
        RuleFor(c => c.SandboxId).NotEmpty();

        RuleFor(c => c.Final).NotNull();

        RuleFor(c => c.Final.VmBootedAtUtc).NotEqual(default(DateTime));

        RuleFor(c => c.Final.FinalisedAtUtc).NotEqual(default(DateTime));

        RuleFor(c => c.Final.SampleCount).GreaterThanOrEqualTo(0);
    }
}
