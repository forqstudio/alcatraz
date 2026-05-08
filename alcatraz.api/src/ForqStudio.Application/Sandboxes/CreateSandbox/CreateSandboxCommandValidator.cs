using FluentValidation;

namespace ForqStudio.Application.Sandboxes.CreateSandbox;

internal sealed class CreateSandboxCommandValidator : AbstractValidator<CreateSandboxCommand>
{
    public CreateSandboxCommandValidator()
    {
        RuleFor(c => c.Vcpus).InclusiveBetween(1, 16);

        RuleFor(c => c.MemoryMib).InclusiveBetween(512, 32768);

        RuleFor(c => c.MemoryMib)
            .Must(memory => memory % 256 == 0)
            .WithMessage("MemoryMib must be a multiple of 256");
    }
}
