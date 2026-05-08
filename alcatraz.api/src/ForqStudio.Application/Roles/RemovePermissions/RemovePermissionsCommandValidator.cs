using FluentValidation;

namespace ForqStudio.Application.Roles.RemovePermissions;

internal sealed class RemovePermissionsCommandValidator : AbstractValidator<RemovePermissionsCommand>
{
    public RemovePermissionsCommandValidator()
    {
        RuleFor(c => c.RoleId).GreaterThan(0);

        RuleFor(c => c.PermissionIds)
            .NotEmpty();
    }
}
