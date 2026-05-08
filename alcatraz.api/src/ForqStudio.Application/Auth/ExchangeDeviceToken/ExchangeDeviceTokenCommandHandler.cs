using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Application.Auth.ExchangeDeviceToken;

internal sealed class ExchangeDeviceTokenCommandHandler(
    IDeviceAuthorizationClient deviceAuthorizationClient
    ) : ICommandHandler<ExchangeDeviceTokenCommand, DeviceTokenResponse>
{
    public Task<Result<DeviceTokenResponse>> Handle(
        ExchangeDeviceTokenCommand request,
        CancellationToken cancellationToken) =>
        deviceAuthorizationClient.ExchangeAsync(request.DeviceCode, cancellationToken);
}
