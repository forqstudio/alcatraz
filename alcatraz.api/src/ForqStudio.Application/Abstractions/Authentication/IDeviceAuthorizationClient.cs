using ForqStudio.Domain.Abstractions;

namespace ForqStudio.Application.Abstractions.Authentication;

public interface IDeviceAuthorizationClient
{
    Task<Result<DeviceAuthorizationResponse>> InitiateAsync(CancellationToken cancellationToken = default);

    Task<Result<DeviceTokenResponse>> ExchangeAsync(
        string deviceCode,
        CancellationToken cancellationToken = default);

    Task<Result<DeviceTokenResponse>> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default);
}

public sealed record DeviceAuthorizationResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresIn,
    int Interval);

public sealed record DeviceTokenResponse(
    string AccessToken,
    string? RefreshToken,
    int ExpiresIn,
    string TokenType,
    string? IdToken);

public static class DeviceAuthErrors
{
    public static readonly Error AuthorizationPending = new(
        "Auth.Device.AuthorizationPending",
        "Authorization has not yet completed");

    public static readonly Error SlowDown = new(
        "Auth.Device.SlowDown",
        "Polling too frequently; increase the interval");

    public static readonly Error ExpiredToken = new(
        "Auth.Device.ExpiredToken",
        "The device code has expired");

    public static readonly Error AccessDenied = new(
        "Auth.Device.AccessDenied",
        "The user denied the authorization request");

    public static readonly Error InitiationFailed = new(
        "Auth.Device.InitiationFailed",
        "Failed to initiate the device authorization flow");

    public static readonly Error ExchangeFailed = new(
        "Auth.Device.ExchangeFailed",
        "Failed to exchange the device code for an access token");

    public static readonly Error RefreshFailed = new(
        "Auth.Device.RefreshFailed",
        "Failed to refresh the access token");

    public static readonly Error InvalidGrant = new(
        "Auth.Device.InvalidGrant",
        "The refresh token is no longer valid");
}
