using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Application.Auth.RefreshDeviceToken;

internal sealed class RefreshDeviceTokenCommandHandler(
    IDeviceAuthorizationClient deviceAuthorizationClient
    ) : ICommandHandler<RefreshDeviceTokenCommand, DeviceTokenResponse>
{
    public Task<Result<DeviceTokenResponse>> Handle(
        RefreshDeviceTokenCommand request,
        CancellationToken cancellationToken) =>
        deviceAuthorizationClient.RefreshAsync(request.RefreshToken, cancellationToken);
}
