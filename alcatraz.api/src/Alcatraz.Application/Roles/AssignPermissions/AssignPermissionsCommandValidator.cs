using FluentValidation;

namespace Alcatraz.Application.Roles.AssignPermissions;

internal sealed class AssignPermissionsCommandValidator : AbstractValidator<AssignPermissionsCommand>
{
    public AssignPermissionsCommandValidator()
    {
        RuleFor(c => c.RoleId).GreaterThan(0);

        RuleFor(c => c.PermissionIds)
            .NotEmpty();
    }
}
