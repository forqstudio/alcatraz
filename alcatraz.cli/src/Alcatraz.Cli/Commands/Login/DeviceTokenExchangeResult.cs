namespace Alcatraz.Cli.Commands.Login;

public sealed record DeviceTokenExchangeResult(
    DeviceTokenResponse? Token,
    DeviceTokenError ErrorKind,
    string? ErrorDetail);
