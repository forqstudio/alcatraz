namespace Alcatraz.Cli.Commands.Login;

public sealed record DeviceTokenResponse(
    string AccessToken,
    string? RefreshToken,
    int ExpiresIn,
    string TokenType,
    string? IdToken);
