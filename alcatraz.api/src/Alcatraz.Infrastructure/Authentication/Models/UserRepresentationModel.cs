using Alcatraz.Domain.Users;

namespace Alcatraz.Infrastructure.Authentication.Models;

public sealed class UserRepresentationModel
{
    // Keycloak's user representation returns Access as a string→bool map
    // (e.g. {"manageGroupMembership": true, "view": true, ...}) — typing this
    // as Dictionary<string, string> previously caused System.Text.Json to
    // throw on every admin GET /users response.
    public Dictionary<string, bool> Access { get; set; }

    public Dictionary<string, List<string>> Attributes { get; set; }

    // ClientRoles is a string→[string] map (client-id → role names).
    public Dictionary<string, List<string>> ClientRoles { get; set; }

    public long? CreatedTimestamp { get; set; }

    public CredentialRepresentationModel[] Credentials { get; set; }

    public string[] DisableableCredentialTypes { get; set; }

    public string Email { get; set; }

    public bool? EmailVerified { get; set; }

    public bool? Enabled { get; set; }

    public string FederationLink { get; set; }

    public string Id { get; set; }

    public string[] Groups { get; set; }

    public string FirstName { get; set; }

    public string LastName { get; set; }

    public int? NotBefore { get; set; }

    public string Origin { get; set; }

    public string[] RealmRoles { get; set; }

    public string[] RequiredActions { get; set; }

    public string Self { get; set; }

    public string ServiceAccountClientId { get; set; }

    public string Username { get; set; }

    internal static UserRepresentationModel FromUser(User user) =>
        new()
        {
            FirstName = user.FirstName.Value,
            LastName = user.LastName.Value,
            Email = user.Email.Value,
            Username = user.Email.Value,
            Enabled = true,
            EmailVerified = true,
            CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Attributes = new Dictionary<string, List<string>>(),
            RequiredActions = Array.Empty<string>()
        };
}