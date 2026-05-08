using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Auth.ExchangeDeviceToken;

public sealed record ExchangeDeviceTokenCommand(string DeviceCode) : ICommand<DeviceTokenResponse>;
