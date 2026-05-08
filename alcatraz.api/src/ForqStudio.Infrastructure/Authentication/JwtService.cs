using System.Net.Http.Json;
using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Infrastructure.Authentication.Models;
using Microsoft.Extensions.Options;

namespace ForqStudio.Infrastructure.Authentication;

internal sealed class JwtService(
    HttpClient _httpClient,
    IOptions<KeycloakOptions> keycloakOptions) : IJwtService
{
    private readonly KeycloakOptions keycloakOptions = keycloakOptions.Value;
    private static readonly Error AuthenticationFailed = new(
        "Keycloak.AuthenticationFailed",
        "Failed to acquire access token do to authentication failure");

    public async Task<Result<string>> GetAccessTokenAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var authRequestParameters = new KeyValuePair<string, string>[]
            {
                new("client_id", keycloakOptions.AuthClientId),
                new("client_secret", keycloakOptions.AuthClientSecret),
                new("scope", "openid email"),
                new("grant_type", "password"),
                new("username", email),
                new("password", password)
            };

            var authorizationRequestContent = new FormUrlEncodedContent(authRequestParameters);

            var response = await _httpClient.PostAsync("", authorizationRequestContent, cancellationToken);

            response.EnsureSuccessStatusCode();

            var authorizationToken = await response.Content.ReadFromJsonAsync<AuthorizationToken>();

            if (authorizationToken is null)
            {
                return Result.Failure<string>(AuthenticationFailed);
            }

            return authorizationToken.AccessToken;
        }
        catch (HttpRequestException)
        {
            return Result.Failure<string>(AuthenticationFailed);
        }
    }
}