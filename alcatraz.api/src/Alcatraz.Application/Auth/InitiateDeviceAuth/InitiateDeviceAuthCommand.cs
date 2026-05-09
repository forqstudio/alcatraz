using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Auth.InitiateDeviceAuth;

public sealed record InitiateDeviceAuthCommand : ICommand<DeviceAuthorizationResponse>;
