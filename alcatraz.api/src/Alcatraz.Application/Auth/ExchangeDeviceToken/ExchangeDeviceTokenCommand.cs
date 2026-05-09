using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Auth.ExchangeDeviceToken;

public sealed record ExchangeDeviceTokenCommand(string DeviceCode) : ICommand<DeviceTokenResponse>;
