using FluentValidation;

namespace ForqStudio.Application.Roles.AssignPermissions;

internal sealed class AssignPermissionsCommandValidator : AbstractValidator<AssignPermissionsCommand>
{
    public AssignPermissionsCommandValidator()
    {
        RuleFor(c => c.RoleId).GreaterThan(0);

        RuleFor(c => c.PermissionIds)
            .NotEmpty();
    }
}
