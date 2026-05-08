using ForqStudio.Domain.Users;
using FluentValidation;

namespace ForqStudio.Application.Roles.CreateRole;

internal sealed class CreateRoleCommandValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleCommandValidator()
    {
        RuleFor(c => c.Name)
            .NotEmpty()
            .MaximumLength(Role.NameMaxLength);

        RuleFor(c => c.PermissionIds)
            .NotNull();
    }
}
