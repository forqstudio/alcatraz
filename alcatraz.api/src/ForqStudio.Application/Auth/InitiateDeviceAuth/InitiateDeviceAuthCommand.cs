using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Auth.InitiateDeviceAuth;

public sealed record InitiateDeviceAuthCommand : ICommand<DeviceAuthorizationResponse>;
