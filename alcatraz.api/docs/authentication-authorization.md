# Authentication & Authorization Documentation

## Overview

ForqStudio implements a **permission-based authorization system** integrated with **Keycloak** as the identity provider. The architecture follows clean architecture principles, separating authentication concerns from authorization logic.

This system supports:
- **JWT-based authentication** via Keycloak
- **Role-based access control (RBAC)** with database-stored roles
- **Permission-based authorization** for fine-grained access control
- **Claim enrichment** via custom claims transformation
- **Caching** for optimized permission lookups

---

## Table of Contents

1. [Authentication](#authentication)
2. [Authorization](#authorization)
3. [Domain Models](#domain-models)
4. [Key Components](#key-components)
5. [Data Flow](#data-flow)
6. [Application Layer](#application-layer-commands--queries)
7. [Usage Examples](#usage-examples)
8. [Configuration](#configuration)
9. [Caching](#caching)
10. [Database Schema](#database-schema)
11. [Security Considerations](#security-considerations)

---

## Authentication

### Identity Provider

ForqStudio uses **Keycloak** as the external identity provider for user authentication.

### User Registration

The `AuthenticationService` handles user registration by creating users in Keycloak with password credentials.

**Location**: `src/ForqStudio.Infrastructure/Authentication/AuthenticationService.cs`

```csharp
public interface IAuthenticationService
{
    Task<string> RegisterAsync(
        User user,
        string password,
        CancellationToken cancellationToken = default);
}
```

**Implementation**: Registers users in Keycloak with a password credential type and returns the identity ID extracted from the Location header.

### Token Generation

The `JwtService` obtains JWT access tokens from Keycloak using the OAuth2 password grant flow.

**Location**: `src/ForqStudio.Infrastructure/Authentication/JwtService.cs`

```csharp
public interface IJwtService
{
    Task<Result<string>> GetAccessTokenAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default);
}
```

**Implementation**: Sends a POST request to Keycloak's token endpoint with:
- `client_id` - Auth client ID
- `client_secret` - Auth client secret
- `grant_type` - "password"
- `username` - User email
- `password` - User password
- `scope` - "openid email"

### User Context

The `UserContext` provides access to the current authenticated user's information from the HTTP context.

**Location**: `src/ForqStudio.Infrastructure/Authentication/UserContext.cs`

```csharp
public interface IUserContext
{
    Guid UserId { get; }
    string IdentityId { get; }
}
```

**Implementation**: Extracts user information from the `HttpContext.User` claims using the custom extension methods:

```csharp
// ClaimsPrincipalExtensions.cs
public static Guid GetUserId(this ClaimsPrincipal? principal)
{
    var userId = principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);
    return Guid.TryParse(userId, out var parsedUserId) ?
        parsedUserId :
        throw new ApplicationException("User identifier is unavailable");
}

public static string GetIdentityId(this ClaimsPrincipal? principal)
{
    return principal?.FindFirstValue(ClaimTypes.NameIdentifier) ??
           throw new ApplicationException("User identity is unavailable");
}
```

### Admin Authorization (Service-to-Service)

The `AdminAuthorizationDelegatingHandler` handles administrative operations that require client credentials flow to obtain admin tokens from Keycloak.

**Location**: `src/ForqStudio.Infrastructure/Authentication/AdminAuthorizationDelegatingHandler.cs`

```csharp
public sealed class AdminAuthorizationDelegatingHandler : DelegatingHandler
{
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
        // ... token acquisition logic
    }
}
```

**Use Case**: Used for admin operations that need to authenticate with Keycloak using client credentials (e.g., user registration, administrative API calls).

---

## Authorization

### Permission-Based Authorization

ForqStudio implements a **custom permission-based authorization system** that dynamically checks user permissions against required permissions.

### Authorization Components

#### HasPermissionAttribute

A custom attribute used to secure API endpoints by requiring specific permissions.

**Location**: `src/ForqStudio.Infrastructure/Authorization/HasPermissionAttribute.cs`

```csharp
public sealed class HasPermissionAttribute(string permission) : AuthorizeAttribute(permission)
{
}
```

**Usage**:
```csharp
[HasPermission(Permissions.UsersWrite)]
public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request)
{
    // Only users with "users.write" permission can access this endpoint
}
```

#### PermissionRequirement

Represents a single permission requirement for authorization.

**Location**: `src/ForqStudio.Infrastructure/Authorization/PermissionRequirement.cs`

```csharp
internal sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}
```

#### PermissionAuthorizationHandler

Handles the authorization check for permission requirements.

**Location**: `src/ForqStudio.Infrastructure/Authorization/PermissionAuthorizationHandler.cs`

```csharp
internal sealed class PermissionAuthorizationHandler(IServiceProvider serviceProvider) 
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.Identity is not { IsAuthenticated: true })
        {
            return;
        }

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

#### PermissionAuthorizationPolicyProvider

Dynamically creates authorization policies for permission requirements.

**Location**: `src/ForqStudio.Infrastructure/Authorization/PermissionAuthorizationPolicyProvider.cs`

```csharp
internal sealed class PermissionAuthorizationPolicyProvider 
    : DefaultAuthorizationPolicyProvider
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) 
    { 
        var policy = await base.GetPolicyAsync(policyName);

        if (policy is not null)
        {
            return policy;
        }

        var permissionPolicy = new AuthorizationPolicyBuilder()
            .AddRequirements(new PermissionRequirement(policyName))
            .Build();

        _authorizationOptions.AddPolicy(policyName, permissionPolicy);

        return permissionPolicy;
    }
}
```

#### CustomClaimsTransformation

Enriches user claims with roles from the database on authentication. This transforms the claims principal after successful authentication to include database-specific role information.

**Location**: `src/ForqStudio.Infrastructure/Authorization/CustomClaimsTransformation.cs`

```csharp
public sealed class CustomClaimsTransformation : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        // Skip if not authenticated or if claims already transformed
        if (principal.Identity is not { IsAuthenticated: true } ||
            principal.HasClaim(claim => claim.Type == ClaimTypes.Role) &&
            principal.HasClaim(claim => claim.Type == JwtRegisteredClaimNames.Sub))
        {
            return principal;
        }

        using var scope = serviceProvider.CreateScope();
        var authorizationService = scope.ServiceProvider.GetRequiredService<AuthorizationService>();
        var identityId = principal.GetIdentityId();
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

**Key Features**:
- Extracts `IdentityId` from Keycloak token (NameIdentifier claim)
- Queries database for user's roles
- Adds the application-specific `UserId` as the JWT Subject claim
- Adds role claims to the principal for ASP.NET Core authorization

#### AuthorizationService

Provides methods to retrieve user roles and permissions, with caching support.

**Location**: `src/ForqStudio.Infrastructure/Authorization/AuthorizationService.cs`

```csharp
internal sealed class AuthorizationService(ApplicationDbContext dbContext, ICacheService cacheService)
{
    public async Task<UserRolesResponse> GetRolesForUserAsync(string identityId)
    {
        var cacheKey = CacheKeys.AuthRoles(identityId);
        var cachedRoles = await cacheService.GetAsync<UserRolesResponse>(cacheKey);

        if (cachedRoles is not null)
        {
            return cachedRoles;
        }

        var roles = await dbContext.Set<User>()
            .Where(u => u.IdentityId == identityId)
            .Select(u => new UserRolesResponse
            {
                UserId = u.Id,
                Roles = u.Roles.ToList()
            })
            .FirstAsync();

        await cacheService.SetAsync(cacheKey, roles);
        return roles;
    }

    public async Task<HashSet<string>> GetPermissionsForUserAsync(string identityId)
    {
        var cacheKey = CacheKeys.AuthPermissions(identityId);
        var cachedPermissions = await cacheService.GetAsync<HashSet<string>>(cacheKey);

        if (cachedPermissions is not null)
        {
            return cachedPermissions;
        }

        var permissions = await dbContext.Set<User>()
            .Where(u => u.IdentityId == identityId)
            .SelectMany(u => u.Roles.Select(r => r.Permissions))
            .FirstAsync();

        var permissionsSet = permissions.Select(p => p.Name).ToHashSet();
        await cacheService.SetAsync(cacheKey, permissionsSet);
        return permissionsSet;
    }
}
```

---

## Domain Models

### User

Represents an application user, linked to Keycloak via `IdentityId`.

**Location**: `src/ForqStudio.Domain/Users/User.cs`

```csharp
public sealed class User : Entity
{
    public FirstName FirstName { get; private set; }
    public LastName LastName { get; private set; }
    public Email Email { get; private set; }
    public string IdentityId { get; private set; }
    public IReadOnlyCollection<Role> Roles => _roles.ToList();

    public static User Create(FirstName firstName, LastName lastName, Email email)
    {
        var user = new User(Guid.NewGuid(), firstName, lastName, email);
        user._roles.Add(Role.User);
        user.RaiseDomainEvent(new UserCreatedDomainEvent(user.Id));
        return user;
    }

    public void SetIdentityId(string identityId)
    {
        IdentityId = identityId;
    }
}
```

### Role

Represents a role that can be assigned to users and contains permissions.

**Location**: `src/ForqStudio.Domain/Users/Role.cs`

```csharp
public sealed class Role
{
    public static readonly Role User = new(1, Roles.User);

    public int Id { get; init; }
    public string Name { get; private set; }
    public ICollection<User> Users { get; init; }
    public ICollection<Permission> Permissions { get; private set; }

    public void AssignPermissions(IList<Permission> permissions) { }
    public void AddPermissions(IList<Permission> permissions) { }
    public void RemovePermissions(IList<int> permissionIds) { }
}
```

### Permission

Represents a granular permission that can be assigned to roles.

**Location**: `src/ForqStudio.Domain/Users/Permission.cs`

### Permissions Static Class

Defines all available permission constants.

**Location**: `src/ForqStudio.Domain/Users/Permissions.cs`

```csharp
public static class Permissions
{
    public const string UsersRead = "users.read";
    public const string UsersWrite = "users.write";
    public const string PermissionsRead = "permissions.read";
    public const string PermissionsWrite = "permissions.write";
    public const string RolesRead = "roles.read";
    public const string RolesWrite = "roles.write";
}
```

### Roles Static Class

Defines all available role constants.

**Location**: `src/ForqStudio.Domain/Users/Roles.cs`

```csharp
public static class Roles
{
    public const string User = "user";
}
```

---

## Data Flow

### Authentication Flow

1. **User Registration**:
   - User submits registration data (email, password, first name, last name)
   - `AuthenticationService.RegisterAsync()` creates user in Keycloak with password credential
   - Keycloak returns user identity ID in Location header
   - Application creates local User record linked to Keycloak identity

2. **User Login (Token Acquisition)**:
   - User submits credentials (email/password) to the login endpoint
   - `JwtService.GetAccessTokenAsync()` sends OAuth2 password grant request to Keycloak
   - Keycloak validates credentials and returns JWT access token
   - Token is returned to the client

3. **Subsequent Requests**:
   - Client includes JWT token in Authorization header (`Bearer {token}`)
   - ASP.NET Core validates JWT token signature and expiration
   - `CustomClaimsTransformation.TransformAsync()` enriches claims with database roles

### Authorization Flow

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          AUTHORIZATION FLOW                                  │
└─────────────────────────────────────────────────────────────────────────────┘

Client Request with JWT
         │
         ▼
┌─────────────────────────┐
│  JWT Token Validation   │
│  (ASP.NET Core Middleware)│
└─────────────────────────┘
         │
         ▼
┌───────────────────────────────────────────────────────────────────────┐
│  CustomClaimsTransformation.TransformAsync()                         │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │  1. Extract IdentityId from token (ClaimTypes.NameIdentifier) │   │
│  │  2. Call AuthorizationService.GetRolesForUserAsync(identityId)│   │
│  │  3. Add UserId as JWT Registered ClaimNames.Sub                │   │
│  │  4. Add Role claims to principal                              │   │
│  └─────────────────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌───────────────────────────────────────────────────────────────────────┐
│  Endpoint Authorization Check                                          │
│  [HasPermission("roles.read")]                                        │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │  1. PermissionAuthorizationPolicyProvider.GetPolicyAsync()    │   │
│  │     creates dynamic policy with PermissionRequirement          │   │
│  │  2. PermissionAuthorizationHandler.HandleRequirementAsync()    │   │
│  │  3. Call AuthorizationService.GetPermissionsForUserAsync()    │   │
│  │  4. Check if required permission exists in user permissions   │   │
│  │  5. context.Succeed(requirement) if authorized               │   │
│  └─────────────────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────────────┘
         │
         ▼
    Access Granted/Denied
```

### Key Points

- **Two Identifiers**: The system uses two different user identifiers:
  - `IdentityId`: External ID from Keycloak (used for authentication)
  - `UserId`: Internal application GUID (used for authorization and domain logic)

- **Claims Transformation**: The transformation runs once per request and adds:
  - `JwtRegisteredClaimNames.Sub` → Application UserId
  - `ClaimTypes.Role` → Role names from database

- **Caching**: Both roles and permissions are cached using the `CacheService` with keys:
  - Roles: `auth:roles:{identityId}`
  - Permissions: `auth:permissions:{identityId}`

---

## Application Layer (Commands & Queries)

The application layer contains MediatR handlers for managing users, roles, and permissions.

### Users

**Location**: `src/ForqStudio.Application/Users/`

- `RegisterUserCommandHandler`: Handles user registration
- `LoginUserCommandHandler`: Handles user login
- `GetLoggedInUserQueryHandler`: Retrieves current user information

### Roles Management

**Location**: `src/ForqStudio.Application/Roles/`

- `CreateRoleCommandHandler`: Creates new roles with optional permissions
- `UpdateRoleCommandHandler`: Updates role details and permissions
- `DeleteRoleCommandHandler`: Soft-deletes roles
- `AssignPermissionsCommandHandler`: Assigns permissions to roles (includes cache invalidation)
- `RemovePermissionsCommandHandler`: Removes permissions from roles
- `GetRolesQueryHandler`: Retrieves all roles with their permissions
- `GetRoleQueryHandler`: Retrieves a specific role by ID

### Permissions Management

**Location**: `src/ForqStudio.Application/Permissions/`

- `CreatePermissionCommandHandler`: Creates new permissions
- `UpdatePermissionCommandHandler`: Updates permission details
- `DeletePermissionCommandHandler`: Soft-deletes permissions
- `GetPermissionsQueryHandler`: Retrieves all permissions
- `GetPermissionQueryHandler`: Retrieves a specific permission by ID

### Cache Invalidation

When permissions are assigned or removed from roles, the authorization cache is automatically invalidated:

```csharp
// In AssignPermissionsCommandHandler
await cacheService.RemoveAsync(CacheKeys.AuthRoles(identityId));
await cacheService.RemoveAsync(CacheKeys.AuthPermissions(identityId));
```

The `[HasPermission]` attribute is used to protect API endpoints. Here's how it's applied in the RolesController and PermissionsController:

```csharp
[ApiController]
[Authorize]
[ApiVersion(ApiVersions.V1)]
[Route("api/v{version:apiVersion}/roles")]
public class RolesController : ControllerBase
{
    [HttpGet]
    [HasPermission(DomainPermissions.RolesRead)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetRolesQuery(), cancellationToken);
        return Ok(result.Value);
    }

    [HttpPost]
    [HasPermission(DomainPermissions.RolesWrite)]
    public async Task<IActionResult> Create(
        CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CreateRoleCommand(request.Name, request.PermissionIds), cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Value }, result.Value);
    }

    [HttpDelete("{id:int}")]
    [HasPermission(DomainPermissions.RolesWrite)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteRoleCommand(id), cancellationToken);
        return result.IsSuccess ? NoContent() : NotFound(result.Error);
    }
}
```

### PermissionsController Example

```csharp
[ApiController]
[Authorize]
[ApiVersion(ApiVersions.V1)]
[Route("api/v{version:apiVersion}/permissions")]
public class PermissionsController : ControllerBase
{
    [HttpGet]
    [HasPermission(DomainPermissions.PermissionsRead)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken) { ... }

    [HttpPost]
    [HasPermission(DomainPermissions.PermissionsWrite)]
    public async Task<IActionResult> Create(CreatePermissionRequest request, ...) { ... }

    [HttpDelete("{id:int}")]
    [HasPermission(DomainPermissions.PermissionsWrite)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken) { ... }
}
```

### Using UserContext in a Service

```csharp
public class SomeService
{
    private readonly IUserContext _userContext;

    public SomeService(IUserContext userContext)
    {
        _userContext = userContext;
    }

    public void DoSomething()
    {
        Guid userId = _userContext.UserId;
        string identityId = _userContext.IdentityId;
    }
}
```

---

## Configuration

### Keycloak Configuration

Keycloak settings are configured in `appsettings.json`:

```json
{
  "Keycloak": {
    "Url": "https://keycloak.example.com",
    "Realm": "forqstudio",
    "AuthClientId": "forqstudio-auth",
    "AuthClientSecret": "your-client-secret",
    "AdminClientId": "forqstudio-admin",
    "AdminClientSecret": "your-admin-secret"
  }
}
```

### Authorization Middleware Registration

The authorization services are registered in the dependency injection container:

```csharp
// Add authorization
services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

// Add custom authorization policy provider
services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();

// Add authorization handlers
services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

// Add claims transformation
services.AddScoped<IClaimsTransformation, CustomClaimsTransformation>();
```

---

## Caching

Roles and permissions are cached using the `CacheService` to improve performance:

- **Roles Cache Key**: `auth:roles:{identityId}`
- **Permissions Cache Key**: `auth:permissions:{identityId}`

### Cache Flow

```csharp
public async Task<HashSet<string>> GetPermissionsForUserAsync(string identityId)
{
    var cacheKey = CacheKeys.AuthPermissions(identityId);
    
    // Check cache first
    var cachedPermissions = await cacheService.GetAsync<HashSet<string>>(cacheKey);
    if (cachedPermissions is not null)
    {
        return cachedPermissions;
    }

    // Query database if not cached
    var permissions = await dbContext.Set<User>()
        .Where(u => u.IdentityId == identityId)
        .SelectMany(u => u.Roles.Select(r => r.Permissions))
        .FirstAsync();

    var permissionsSet = permissions.Select(p => p.Name).ToHashSet();
    
    // Store in cache for future requests
    await cacheService.SetAsync(cacheKey, permissionsSet);
    
    return permissionsSet;
}
```

### Cache Invalidation

Cache invalidation occurs automatically when permissions are modified:

1. **Assigning Permissions to Role**: 
   - `AssignPermissionsCommandHandler` invalidates cache for all users with that role

2. **Removing Permissions from Role**:
   - `RemovePermissionsCommandHandler` invalidates cache for all users with that role

3. **Updating/Deleting Roles**:
   - Cache is invalidated for users assigned to the affected role

### Cache Configuration

The cache can be configured in `appsettings.json`:

```json
{
  "CacheOptions": {
    "AbsoluteExpirationRelativeToNow": "00:30:00",  // 30 minutes
    "SlidingExpiration": "00:15:00"                  // 15 minutes sliding
  }
}
```

---

## Database Schema

### Entity Relationships

The authorization system consists of four tables with the following relationships:

```
User ──────< RolePermission >────── Permission
  │                  │
  │                  │
  └──────────────────┘
        Role
```

**Relationship Details:**

| Relationship | Type | Description |
|--------------|------|-------------|
| User → Role | One-to-Many | A user can have multiple roles |
| Role → Permission | Many-to-Many | A role can have multiple permissions (via RolePermission junction table) |
| User → Permission | Derived | User permissions are derived from their assigned roles |

**Key Points:**
- Users are linked to Keycloak via the `IdentityId` field
- Permissions are never assigned directly to users - always through roles
- The `RolePermission` junction table connects roles and permissions

---

## Security Considerations

### Authentication Security

1. **Password Handling**: Passwords are never stored in the application database - they're managed by Keycloak
2. **JWT Token Security**: Tokens are validated for signature, expiration, and issuer
3. **HTTPS Only**: Always use HTTPS in production

### Authorization Security

1. **Principle of Least Privilege**: Only grant the minimum permissions necessary
2. **Permission Validation**: Permissions are always checked against the database (not just token claims)
3. **Cache Security**: Cache doesn't store sensitive data - only permission names

### Best Practices

1. **Always use `[HasPermission]` attribute** on protected endpoints
2. **Don't rely on role claims alone** - always use permissions for fine-grained access
3. **Implement proper cache invalidation** when roles/permissions change
4. **Audit role and permission changes** for security compliance
5. **Regularly review user role assignments** to remove unnecessary access
