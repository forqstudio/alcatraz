using FluentValidation;

namespace ForqStudio.Application.UserSettings.UpdateUserSettings;

internal sealed class UpdateUserSettingsCommandValidator : AbstractValidator<UpdateUserSettingsCommand>
{
    public UpdateUserSettingsCommandValidator()
    {
        RuleFor(c => c.PreferredLanguage)
            .MaximumLength(10)
            .When(c => c.PreferredLanguage is not null);

        RuleFor(c => c.Timezone)
            .MaximumLength(100)
            .When(c => c.Timezone is not null);
    }
}
