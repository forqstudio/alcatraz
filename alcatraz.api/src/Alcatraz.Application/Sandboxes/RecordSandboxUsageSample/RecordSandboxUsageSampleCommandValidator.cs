using FluentValidation;

namespace Alcatraz.Application.Sandboxes.RecordSandboxUsageSample;

internal sealed class RecordSandboxUsageSampleCommandValidator
    : AbstractValidator<RecordSandboxUsageSampleCommand>
{
    public RecordSandboxUsageSampleCommandValidator()
    {
        RuleFor(c => c.SandboxId).NotEmpty();

        RuleFor(c => c.SampledAtUtc).NotEqual(default(DateTime));
    }
}
