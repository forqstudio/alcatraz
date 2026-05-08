using System.Net.Http.Json;
using System.Text.Json;
using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Infrastructure.Authentication.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForqStudio.Infrastructure.Authentication;

internal sealed class KeycloakDeviceAuthorizationClient(
    HttpClient httpClient,
    IOptions<KeycloakOptions> keycloakOptions,
    ILogger<KeycloakDeviceAuthorizationClient> logger
    ) : IDeviceAuthorizationClient
{
    private const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly KeycloakOptions keycloakOptions = keycloakOptions.Value;

    public async Task<Result<DeviceAuthorizationResponse>> InitiateAsync(CancellationToken cancellationToken = default)
    {
        var parameters = new[]
        {
            new KeyValuePair<string, string>("client_id", keycloakOptions.AuthClientId),
            new KeyValuePair<string, string>("client_secret", keycloakOptions.AuthClientSecret),
            new KeyValuePair<string, string>("scope", "openid email profile"),
        };

        try
        {
            using var content = new FormUrlEncodedContent(parameters);
            using var response = await httpClient.PostAsync(
                keycloakOptions.DeviceAuthorizationUrl,
                content,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Device authorization initiation failed with status {Status}: {Body}",
                    response.StatusCode,
                    body);
                return Result.Failure<DeviceAuthorizationResponse>(DeviceAuthErrors.InitiationFailed);
            }

            var payload = await response.Content.ReadFromJsonAsync<DeviceAuthorizationKeycloakResponse>(
                cancellationToken: cancellationToken);

            if (payload is null || string.IsNullOrEmpty(payload.DeviceCode))
            {
                return Result.Failure<DeviceAuthorizationResponse>(DeviceAuthErrors.InitiationFailed);
            }

            return new DeviceAuthorizationResponse(
                payload.DeviceCode,
                payload.UserCode,
                payload.VerificationUri,
                payload.VerificationUriComplete ?? payload.VerificationUri,
                payload.ExpiresIn,
                payload.Interval <= 0 ? 5 : payload.Interval);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Device authorization initiation transport error");
            return Result.Failure<DeviceAuthorizationResponse>(DeviceAuthErrors.InitiationFailed);
        }
    }

    public async Task<Result<DeviceTokenResponse>> ExchangeAsync(
        string deviceCode,
        CancellationToken cancellationToken = default)
    {
        var parameters = new[]
        {
            new KeyValuePair<string, string>("grant_type", DeviceCodeGrantType),
            new KeyValuePair<string, string>("device_code", deviceCode),
            new KeyValuePair<string, string>("client_id", keycloakOptions.AuthClientId),
            new KeyValuePair<string, string>("client_secret", keycloakOptions.AuthClientSecret),
        };

        try
        {
            using var content = new FormUrlEncodedContent(parameters);
            using var response = await httpClient.PostAsync(
                keycloakOptions.TokenUrl,
                content,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var success = await response.Content.ReadFromJsonAsync<DeviceTokenKeycloakResponse>(
                    cancellationToken: cancellationToken);

                if (success is null || string.IsNullOrEmpty(success.AccessToken))
                {
                    return Result.Failure<DeviceTokenResponse>(DeviceAuthErrors.ExchangeFailed);
                }

                return new DeviceTokenResponse(
                    success.AccessToken,
                    success.RefreshToken,
                    success.ExpiresIn,
                    success.TokenType,
                    success.IdToken);
            }

            KeycloakErrorResponse? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<KeycloakErrorResponse>(
                    cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                // fall through to generic error
            }

            return Result.Failure<DeviceTokenResponse>(MapError(error?.Error));
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Device token exchange transport error");
            return Result.Failure<DeviceTokenResponse>(DeviceAuthErrors.ExchangeFailed);
        }
    }

    private static Error MapError(string? keycloakError) =>
        keycloakError switch
        {
            "authorization_pending" => DeviceAuthErrors.AuthorizationPending,
            "slow_down" => DeviceAuthErrors.SlowDown,
            "expired_token" => DeviceAuthErrors.ExpiredToken,
            "access_denied" => DeviceAuthErrors.AccessDenied,
            _ => DeviceAuthErrors.ExchangeFailed,
        };
}
