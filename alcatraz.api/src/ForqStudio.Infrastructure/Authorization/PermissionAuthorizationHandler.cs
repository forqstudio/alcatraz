using ForqStudio.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace ForqStudio.Infrastructure.Authorization;

internal sealed class PermissionAuthorizationHandler(IServiceProvider serviceProvider) : AuthorizationHandler<PermissionRequirement>
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
