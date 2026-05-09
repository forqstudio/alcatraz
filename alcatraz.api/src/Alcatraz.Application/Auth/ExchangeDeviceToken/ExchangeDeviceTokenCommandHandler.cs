using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Application.Auth.ExchangeDeviceToken;

internal sealed class ExchangeDeviceTokenCommandHandler(
    IDeviceAuthorizationClient deviceAuthorizationClient
    ) : ICommandHandler<ExchangeDeviceTokenCommand, DeviceTokenResponse>
{
    public Task<Result<DeviceTokenResponse>> Handle(
        ExchangeDeviceTokenCommand request,
        CancellationToken cancellationToken) =>
        deviceAuthorizationClient.ExchangeAsync(request.DeviceCode, cancellationToken);
}
