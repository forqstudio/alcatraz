using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Auth.RefreshDeviceToken;

public sealed record RefreshDeviceTokenCommand(string RefreshToken) : ICommand<DeviceTokenResponse>;
