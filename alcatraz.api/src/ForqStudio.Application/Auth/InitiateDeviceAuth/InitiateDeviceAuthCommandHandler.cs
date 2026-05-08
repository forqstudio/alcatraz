using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Application.Auth.InitiateDeviceAuth;

internal sealed class InitiateDeviceAuthCommandHandler(
    IDeviceAuthorizationClient deviceAuthorizationClient
    ) : ICommandHandler<InitiateDeviceAuthCommand, DeviceAuthorizationResponse>
{
    public Task<Result<DeviceAuthorizationResponse>> Handle(
        InitiateDeviceAuthCommand request,
        CancellationToken cancellationToken) =>
        deviceAuthorizationClient.InitiateAsync(cancellationToken);
}
