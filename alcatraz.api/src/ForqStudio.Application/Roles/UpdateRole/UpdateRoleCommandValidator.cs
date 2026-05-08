using ForqStudio.Domain.Users;
using FluentValidation;

namespace ForqStudio.Application.Roles.UpdateRole;

internal sealed class UpdateRoleCommandValidator : AbstractValidator<UpdateRoleCommand>
{
    public UpdateRoleCommandValidator()
    {
        RuleFor(c => c.Id).GreaterThan(0);

        RuleFor(c => c.Name)
            .NotEmpty()
            .MaximumLength(Role.NameMaxLength);

        RuleFor(c => c.PermissionIds)
            .NotNull();
    }
}
