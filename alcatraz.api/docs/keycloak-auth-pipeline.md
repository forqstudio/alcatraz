# Keycloak Authentication & Fine-Grained Authorization in ASP.NET Core

A learning project demonstrating how to integrate **Keycloak** into ASP.NET Core's authentication and authorization middleware with a **fine-grained roles and permissions system** — built on Clean Architecture with CQRS and DDD.

The booking/apartment domain is incidental. The core focus is the auth pipeline.

---

## What This Covers

- Integrating Keycloak as an external identity provider with JWT Bearer authentication
- Extending ASP.NET Core's claims pipeline to enrich tokens with database-managed roles
- Building a fine-grained permission system on top of ASP.NET Core's authorization middleware
- Dynamic policy creation so you can protect endpoints with a single attribute
- Caching roles and permissions in Redis to avoid database hits on every request

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│                    Keycloak (IdP)                    │
│   Issues JWT tokens · Stores user identities only   │
└──────────────────────────┬──────────────────────────┘
                           │ JWT (contains IdentityId)
                           ▼
┌─────────────────────────────────────────────────────┐
│                   ASP.NET Core API                   │
│                                                      │
│  1. JWT Bearer middleware validates the token        │
│  2. CustomClaimsTransformation enriches the          │
│     ClaimsPrincipal with roles from the database     │
│  3. [HasPermission] attribute triggers a dynamic     │
│     authorization policy check                       │
│  4. PermissionAuthorizationHandler checks if         │
│     the user's roles grant the required permission   │
└──────────────────────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────┐
│               PostgreSQL + Redis                     │
│   Roles & permissions stored in DB · Cached in Redis │
└─────────────────────────────────────────────────────┘
```

**Key design decision:** Keycloak owns identity (who you are). The application database owns authorization (what you can do). Roles and permissions are managed entirely within the app, not inside Keycloak.

---

## The Permission Model

Roles and permissions live in the domain layer.

```csharp
// Domain/Users/Permissions.cs
public static class Permissions
{
    public const string UsersRead       = "users.read";
    public const string UsersWrite      = "users.write";
    public const string PermissionsRead = "permissions.read";
    public const string PermissionsWrite = "permissions.write";
    public const string RolesRead       = "roles.read";
    public const string RolesWrite      = "roles.write";
}
```

A `Role` owns a collection of `Permission` objects. Users are assigned one or more roles.

```csharp
// Domain/Users/Role.cs
public sealed class Role
{
    public int Id { get; init; }
    public string Name { get; private set; }
    public ICollection<Permission> Permissions { get; private set; }

    public void AddPermissions(IList<Permission> permissions) { ... }
    public void RemovePermissions(IList<int> permissionIds) { ... }
    public void AssignPermissions(IList<Permission> permissions) { ... }
}
```

The relationship is: **User → Roles → Permissions**. To check if a user can do something, you walk that chain.

---

## Authentication Flow

### 1. User Registration

When a user registers, the app creates them in **both** Keycloak and the local database:

```
POST /api/v1/users/register
{
  "email": "alice@example.com",
  "firstName": "Alice",
  "lastName": "Smith",
  "password": "secret"
}
```

Internally, `RegisterUserCommandHandler` calls `IAuthenticationService.RegisterAsync`, which POSTs to Keycloak's Admin API:

```csharp
// Infrastructure/Authentication/AuthenticationService.cs
public async Task<string> RegisterAsync(User user, string password, CancellationToken cancellationToken = default)
{
    var userRepresentationModel = UserRepresentationModel.FromUser(user);

    userRepresentationModel.Credentials = new CredentialRepresentationModel[]
    {
        new() { Value = password, Temporary = false, Type = "password" }
    };

    var response = await httpClient.PostAsJsonAsync("users", userRepresentationModel, cancellationToken);

    return ExtractIdentityIdFromLocationHeader(response); // returns the Keycloak user ID
}
```

Keycloak returns the new user's ID in the `Location` header. That ID (`IdentityId`) is stored on the local `User` entity and is the bridge between the two systems.

### 2. Login — Getting a JWT

```
POST /api/v1/users/login
{
  "email": "alice@example.com",
  "password": "secret"
}
```

The app exchanges the credentials for a token via Keycloak's token endpoint using the **Resource Owner Password Credentials** grant:

```csharp
// Infrastructure/Authentication/JwtService.cs
public async Task<Result<string>> GetAccessTokenAsync(string email, string password, CancellationToken cancellationToken = default)
{
    var authRequestParameters = new KeyValuePair<string, string>[]
    {
        new("client_id",     keycloakOptions.AuthClientId),
        new("client_secret", keycloakOptions.AuthClientSecret),
        new("scope",         "openid email"),
        new("grant_type",    "password"),
        new("username",      email),
        new("password",      password)
    };

    var response = await _httpClient.PostAsync("", new FormUrlEncodedContent(authRequestParameters), cancellationToken);
    response.EnsureSuccessStatusCode();

    var authorizationToken = await response.Content.ReadFromJsonAsync<AuthorizationToken>();
    return authorizationToken.AccessToken;
}
```

The JWT returned by Keycloak contains the user's `IdentityId` in the `sub` claim. It does **not** contain roles or permissions — those come from the database.

### 3. JWT Validation

JWT Bearer middleware is configured to validate tokens against Keycloak's OIDC discovery document:

```csharp
// Infrastructure/Authentication/JwtBearerOptionsSetup.cs
public void Configure(JwtBearerOptions options)
{
    options.Audience            = _authenticationOptions.Audience;
    options.MetadataAddress     = _authenticationOptions.MetadataUrl; // Keycloak OIDC discovery endpoint
    options.RequireHttpsMetadata = _authenticationOptions.RequireHttpsMetadata;
    options.TokenValidationParameters.ValidIssuer = _authenticationOptions.Issuer;
}
```

```jsonc
// appsettings.Development.json
"Authentication": {
  "Audience":             "account",
  "ValidIssuer":          "http://forqstudio-idp:8080/realms/forqstudio",
  "MetadataUrl":          "http://forqstudio-idp:8080/realms/forqstudio/.well-known/openid-configuration",
  "RequireHttpsMetadata": false
}
```

---

## Authorization Pipeline

After the JWT is validated, ASP.NET Core's `IClaimsTransformation` runs before any endpoint handler. This is where the app enriches the token with database-managed roles.

### Step 1 — Claims Transformation (Enriching the Token)

```csharp
// Infrastructure/Authorization/CustomClaimsTransformation.cs
internal sealed class CustomClaimsTransformation(IServiceProvider serviceProvider) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        // Skip if already enriched or not authenticated
        if (principal.Identity is not { IsAuthenticated: true } ||
            principal.HasClaim(c => c.Type == ClaimTypes.Role) &&
            principal.HasClaim(c => c.Type == JwtRegisteredClaimNames.Sub))
        {
            return principal;
        }

        using var scope = serviceProvider.CreateScope();
        var authorizationService = scope.ServiceProvider.GetRequiredService<AuthorizationService>();

        var identityId = principal.GetIdentityId(); // extracts "sub" from the JWT

        var userRoles = await authorizationService.GetRolesForUserAsync(identityId);

        var claimsIdentity = new ClaimsIdentity();
        claimsIdentity.AddClaim(new Claim(JwtRegisteredClaimNames.Sub, userRoles.UserId.ToString()));

        foreach (var role in userRoles.Roles)
        {
            claimsIdentity.AddClaim(new Claim(ClaimTypes.Role, role.Name));
        }

        principal.AddIdentity(claimsIdentity);
        return principal;
    }
}
```

After this runs, the `ClaimsPrincipal` has:
- The internal `UserId` (Guid from the app database) as `sub`
- `ClaimTypes.Role` claims for each role assigned to the user

### Step 2 — The `[HasPermission]` Attribute

Endpoints are protected with a custom attribute:

```csharp
// Infrastructure/Authorization/HasPermissionAttribute.cs
public sealed class HasPermissionAttribute(string permission) : AuthorizeAttribute(permission)
{
}
```

It extends `AuthorizeAttribute` and passes the permission name as the **policy name**. Usage:

```csharp
// Api/Controllers/Users/UsersController.cs
[HttpGet("me")]
[HasPermission(Permissions.UsersRead)]  // "users.read"
public async Task<IActionResult> GetLoggedInUser(CancellationToken cancellationToken)
{
    var result = await sender.Send(new GetLoggedInUserQuery(), cancellationToken);
    return Ok(result.Value);
}
```

### Step 3 — Dynamic Policy Creation

ASP.NET Core's default policy provider only knows about policies registered at startup. A custom provider creates them on-demand from the permission name:

```csharp
// Infrastructure/Authorization/PermissionAuthorizationPolicyProvider.cs
internal sealed class PermissionAuthorizationPolicyProvider : DefaultAuthorizationPolicyProvider
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        // Return existing policy if already registered
        var policy = await base.GetPolicyAsync(policyName);
        if (policy is not null) return policy;

        // Build a new policy on the fly for the requested permission
        var permissionPolicy = new AuthorizationPolicyBuilder()
            .AddRequirements(new PermissionRequirement(policyName))
            .Build();

        _authorizationOptions.AddPolicy(policyName, permissionPolicy); // cache it
        return permissionPolicy;
    }
}
```

The `PermissionRequirement` simply carries the permission string:

```csharp
// Infrastructure/Authorization/PermissionRequirement.cs
internal sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;
```

### Step 4 — Permission Check

The handler resolves the user's permissions and checks if the required one is present:

```csharp
// Infrastructure/Authorization/PermissionAuthorizationHandler.cs
internal sealed class PermissionAuthorizationHandler(IServiceProvider serviceProvider)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.Identity is not { IsAuthenticated: true }) return;

        var scope = serviceProvider.CreateScope();
        var authorizationService = scope.ServiceProvider.GetRequiredService<AuthorizationService>();

        var identityId = context.User.GetIdentityId();
        HashSet<string> permissions = await authorizationService.GetPermissionsForUserAsync(identityId);

        if (permissions.Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}
```

### Step 5 — Database Queries with Redis Caching

Both roles and permissions are fetched from the database and cached in Redis to avoid repeated lookups on every request:

```csharp
// Infrastructure/Authorization/AuthorizationService.cs
public async Task<HashSet<string>> GetPermissionsForUserAsync(string identityId)
{
    var cacheKey = CacheKeys.AuthPermissions(identityId);
    var cached = await cacheService.GetAsync<HashSet<string>>(cacheKey);
    if (cached is not null) return cached;

    var permissions = await dbContext.Set<User>()
        .Where(u => u.IdentityId == identityId)
        .SelectMany(u => u.Roles.Select(r => r.Permissions))
        .FirstAsync();

    var permissionsSet = permissions.Select(p => p.Name).ToHashSet();

    await cacheService.SetAsync(cacheKey, permissionsSet);
    return permissionsSet;
}
```

---

## Full Request Flow (End to End)

```
Client
  │
  │  GET /api/v1/users/me
  │  Authorization: Bearer <jwt>
  ▼
JWT Bearer Middleware
  │  Validates token signature against Keycloak JWKS
  │  Sets principal with IdentityId ("sub" claim from Keycloak)
  ▼
CustomClaimsTransformation
  │  Looks up User by IdentityId → fetches Roles from DB (or Redis)
  │  Adds ClaimTypes.Role claims + internal UserId to principal
  ▼
Authorization Middleware
  │  Sees [HasPermission("users.read")]
  │  Calls PermissionAuthorizationPolicyProvider.GetPolicyAsync("users.read")
  │  Policy contains PermissionRequirement("users.read")
  ▼
PermissionAuthorizationHandler
  │  Fetches permissions for user from DB (or Redis)
  │  Checks if "users.read" is in the set
  │  Calls context.Succeed() if yes
  ▼
Controller Action
  │  Executes GetLoggedInUserQuery via MediatR
  ▼
200 OK — User data returned
```

---

## Dependency Injection Registration

Everything is wired up in `Infrastructure/DependencyInjection.cs`:

```csharp
private static void AddAuthentication(IServiceCollection services, IConfiguration configuration)
{
    services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer();

    services.Configure<AuthenticationOptions>(configuration.GetSection("Authentication"));
    services.ConfigureOptions<JwtBearerOptionsSetup>(); // applies Keycloak settings to JwtBearerOptions

    services.Configure<KeycloakOptions>(configuration.GetSection("Keycloak"));

    // Admin API client (for registering users) — uses client credentials to get an admin token
    services.AddHttpClient<IAuthenticationService, AuthenticationService>(...)
            .AddHttpMessageHandler<AdminAuthorizationDelegatingHandler>();

    // Token endpoint client (for user login)
    services.AddHttpClient<IJwtService, JwtService>(...);

    services.AddScoped<IUserContext, UserContext>();
}

private static void AddAuthorization(IServiceCollection services)
{
    services.AddScoped<AuthorizationService>();
    services.AddTransient<IClaimsTransformation, CustomClaimsTransformation>();
    services.AddTransient<IAuthorizationHandler, PermissionAuthorizationHandler>();
    services.AddTransient<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
}
```

---

## Keycloak Configuration

Two clients are configured in Keycloak:

| Client | Purpose | Grant Type |
|---|---|---|
| `forqstudio-auth-client` | User login (get JWT) | Resource Owner Password Credentials |
| `forqstudio-admin-client` | Registering users via Admin API | Client Credentials |

```jsonc
// appsettings.Development.json
"Keycloak": {
  "BaseUrl":           "http://forqstudio-idp:8080",
  "AdminUrl":          "http://forqstudio-idp:8080/admin/realms/forqstudio/",
  "TokenUrl":          "http://forqstudio-idp:8080/realms/forqstudio/protocol/openid-connect/token",
  "AdminClientId":     "forqstudio-admin-client",
  "AdminClientSecret": "<secret>",
  "AuthClientId":      "forqstudio-auth-client",
  "AuthClientSecret":  "<secret>"
}
```

A Keycloak realm export is included at `.files/forqstudio-realm-export.json` and is automatically imported on startup via Docker Compose.

---

## Running the Project

**Prerequisites:** Docker Desktop

```bash
# Start all infrastructure (Keycloak, PostgreSQL, Redis, Seq)
docker-compose up -d

# The API is available at http://localhost:5000
# Keycloak admin console: http://localhost:8082 (admin / admin)
# Seq log viewer: http://localhost:5341
```

**Database migrations** run automatically on startup.

---

## Project Structure

```
src/
├── ForqStudio.Domain/
│   └── Users/
│       ├── Permission.cs       # Permission entity
│       ├── Permissions.cs      # Permission name constants
│       ├── Role.cs             # Role entity with permission management
│       ├── Roles.cs            # Role name constants
│       ├── User.cs             # User aggregate
│       ├── IPermissionRepository.cs
│       └── IRoleRepository.cs
├── ForqStudio.Application/
│   └── Users/                  # Register, Login, GetLoggedInUser use cases
├── ForqStudio.Infrastructure/
│   ├── Authentication/
│   │   ├── AuthenticationService.cs        # Keycloak Admin API (user registration)
│   │   ├── JwtService.cs                   # Keycloak token endpoint (login)
│   │   ├── JwtBearerOptionsSetup.cs        # JWT validation configuration
│   │   ├── AdminAuthorizationDelegatingHandler.cs  # Attaches admin token to outgoing requests
│   │   └── UserContext.cs                  # IUserContext implementation
│   └── Authorization/
│       ├── AuthorizationService.cs                  # DB queries for roles & permissions
│       ├── CustomClaimsTransformation.cs            # Enriches ClaimsPrincipal with DB roles
│       ├── HasPermissionAttribute.cs                # [HasPermission("...")] attribute
│       ├── PermissionAuthorizationHandler.cs        # Evaluates permission requirements
│       ├── PermissionAuthorizationPolicyProvider.cs # Creates policies on demand
│       └── PermissionRequirement.cs                 # IAuthorizationRequirement
└── ForqStudio.Api/
    └── Controllers/
        ├── Users/          # Register, Login, GetLoggedInUser
        ├── Roles/          # CRUD for roles + permission assignment
        └── Permissions/    # CRUD for permissions
```

---

## Tech Stack

| Concern | Technology |
|---|---|
| Identity Provider | Keycloak |
| Authentication | ASP.NET Core JWT Bearer |
| Authorization | ASP.NET Core Authorization middleware (custom) |
| Database | PostgreSQL + Entity Framework Core |
| Caching | Redis (StackExchange.Redis) |
| Mediator / CQRS | MediatR |
| Logging | Serilog + Seq |
| Background Jobs | Quartz.NET (outbox pattern) |
| Testing | xUnit + NSubstitute + FluentAssertions |
