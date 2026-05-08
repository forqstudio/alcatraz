using FluentValidation;

namespace ForqStudio.Application.Auth.RefreshDeviceToken;

internal sealed class RefreshDeviceTokenCommandValidator : AbstractValidator<RefreshDeviceTokenCommand>
{
    public RefreshDeviceTokenCommandValidator()
    {
        RuleFor(c => c.RefreshToken).NotEmpty().MaximumLength(8192);
    }
}
