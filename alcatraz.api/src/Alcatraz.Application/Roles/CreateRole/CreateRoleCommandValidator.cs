using Alcatraz.Domain.Users;
using FluentValidation;

namespace Alcatraz.Application.Roles.CreateRole;

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
