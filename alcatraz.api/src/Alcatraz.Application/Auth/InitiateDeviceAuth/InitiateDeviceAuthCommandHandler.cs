using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Application.Auth.InitiateDeviceAuth;

internal sealed class InitiateDeviceAuthCommandHandler(
    IDeviceAuthorizationClient deviceAuthorizationClient
    ) : ICommandHandler<InitiateDeviceAuthCommand, DeviceAuthorizationResponse>
{
    public Task<Result<DeviceAuthorizationResponse>> Handle(
        InitiateDeviceAuthCommand request,
        CancellationToken cancellationToken) =>
        deviceAuthorizationClient.InitiateAsync(cancellationToken);
}
