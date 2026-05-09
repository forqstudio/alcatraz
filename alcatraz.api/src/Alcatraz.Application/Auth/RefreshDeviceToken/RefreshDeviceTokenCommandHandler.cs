using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Application.Auth.RefreshDeviceToken;

internal sealed class RefreshDeviceTokenCommandHandler(
    IDeviceAuthorizationClient deviceAuthorizationClient
    ) : ICommandHandler<RefreshDeviceTokenCommand, DeviceTokenResponse>
{
    public Task<Result<DeviceTokenResponse>> Handle(
        RefreshDeviceTokenCommand request,
        CancellationToken cancellationToken) =>
        deviceAuthorizationClient.RefreshAsync(request.RefreshToken, cancellationToken);
}
