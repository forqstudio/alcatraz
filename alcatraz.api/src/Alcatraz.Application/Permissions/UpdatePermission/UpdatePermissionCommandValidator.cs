using Alcatraz.Domain.Users;
using FluentValidation;

namespace Alcatraz.Application.Permissions.UpdatePermission;

internal sealed class UpdatePermissionCommandValidator : AbstractValidator<UpdatePermissionCommand>
{
    public UpdatePermissionCommandValidator()
    {
        RuleFor(c => c.Id).GreaterThan(0);

        RuleFor(c => c.Name)
            .NotEmpty()
            .MaximumLength(Permission.NameMaxLength)
            .Matches(Permission.NameRegexPattern)
            .WithMessage(Permission.NameFormatMessage);
    }
}
