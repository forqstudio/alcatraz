using System.Net.Http.Headers;
using System.Net.Http.Json;
using Alcatraz.Infrastructure.Authentication.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Alcatraz.Infrastructure.Authentication;

/// <summary>
/// Acquires a Keycloak admin client_credentials token and attaches it as the
/// Authorization header on outbound requests. Scope is intentionally narrow:
/// it adds the bearer header and forwards the response untouched.
///
/// **Status checking is the caller's responsibility.** This handler does not
/// throw on non-2xx because some callers (e.g. idempotent register, which
/// treats 409 Conflict as a normal "user already exists" branch) need to
/// inspect the status code themselves. Callers must call
/// <c>EnsureSuccessStatusCode()</c>, check <c>IsSuccessStatusCode</c>, or
/// otherwise handle every status they don't explicitly accept.
/// </summary>
public sealed class AdminAuthorizationDelegatingHandler(IOptions<KeycloakOptions> keycloakOptions) : DelegatingHandler
{
    private readonly KeycloakOptions keycloakOptions = keycloakOptions.Value;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var authorizationToken = await GetAuthorizationToken(cancellationToken);

        request.Headers.Authorization = new AuthenticationHeaderValue(
            JwtBearerDefaults.AuthenticationScheme,
            authorizationToken.AccessToken);

        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<AuthorizationToken> GetAuthorizationToken(CancellationToken cancellationToken)
    {
        var authorizationRequestParameters = new KeyValuePair<string, string>[]
        {
            new("client_id", keycloakOptions.AdminClientId),
            new("client_secret", keycloakOptions.AdminClientSecret),
            new("scope", "openid email"),
            new("grant_type", "client_credentials")
        };

        var authorizationRequestContent = new FormUrlEncodedContent(authorizationRequestParameters);

        var authorizationRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(keycloakOptions.TokenUrl))
        {
            Content = authorizationRequestContent
        };

        var authorizationResponse = await base.SendAsync(authorizationRequest, cancellationToken);

        authorizationResponse.EnsureSuccessStatusCode();

        return await authorizationResponse.Content.ReadFromJsonAsync<AuthorizationToken>() ??
               throw new ApplicationException();
    }
}