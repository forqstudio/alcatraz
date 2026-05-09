using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Domain.Users;
using Alcatraz.Infrastructure.Authentication.Models;
using System.Net;
using System.Net.Http.Json;

namespace Alcatraz.Infrastructure.Authentication;

internal sealed class AuthenticationService(HttpClient httpClient) : IAuthenticationService
{
    private const string PasswordCredentialType = "password";

    public async Task<string> RegisterAsync(
        User user,
        string password,
        CancellationToken cancellationToken = default)
    {
        var userRepresentationModel = UserRepresentationModel.FromUser(user);

        userRepresentationModel.Credentials = new CredentialRepresentationModel[]
        {
            new()
            {
                Value = password,
                Temporary = false,
                Type = PasswordCredentialType
            }
        };

        var response = await httpClient.PostAsJsonAsync(
            "users",
            userRepresentationModel,
            cancellationToken);

        // Keycloak returns 409 when a user with the same email/username already
        // exists. Treat register as idempotent: look the user up and return the
        // existing identity id so the caller can reconcile its local row.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await FindUserIdByEmailAsync(user.Email.Value, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }
        }

        response.EnsureSuccessStatusCode();

        return ExtractIdentityIdFromLocationHeader(response);
    }

    private async Task<string?> FindUserIdByEmailAsync(
        string email,
        CancellationToken cancellationToken)
    {
        var lookup = await httpClient.GetFromJsonAsync<UserRepresentationModel[]>(
            $"users?email={Uri.EscapeDataString(email)}&exact=true",
            cancellationToken);

        var match = lookup?.FirstOrDefault(u =>
            string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));

        return match?.Id;
    }

    private static string ExtractIdentityIdFromLocationHeader(
        HttpResponseMessage httpResponseMessage)
    {
        const string usersSegmentName = "users/";

        var locationHeader = httpResponseMessage.Headers.Location?.PathAndQuery;

        if (locationHeader is null)
        {
            throw new InvalidOperationException("Location header can't be null");
        }

        var userSegmentValueIndex = locationHeader.IndexOf(
            usersSegmentName,
            StringComparison.InvariantCultureIgnoreCase);

        var userIdentityId = locationHeader.Substring(
            userSegmentValueIndex + usersSegmentName.Length);

        return userIdentityId;
    }
}