using FluentValidation;

namespace Alcatraz.Application.Auth.ExchangeDeviceToken;

internal sealed class ExchangeDeviceTokenCommandValidator : AbstractValidator<ExchangeDeviceTokenCommand>
{
    public ExchangeDeviceTokenCommandValidator()
    {
        RuleFor(c => c.DeviceCode).NotEmpty().MaximumLength(512);
    }
}
