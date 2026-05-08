namespace Alcatraz.Cli.Common.Authentication;

public sealed record StoredTokens(
    string AccessToken,
    string? RefreshToken,
    DateTime AccessTokenExpiresAtUtc,
    string TokenType,
    string? IdToken);
