using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Auth.RefreshDeviceToken;

public sealed record RefreshDeviceTokenCommand(string RefreshToken) : ICommand<DeviceTokenResponse>;
