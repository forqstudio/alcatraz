using FluentAssertions;

namespace ForqStudio.Application.UnitTests.UserSettings;

public class UserSettingsUpdateTests
{
    [Fact]
    public void Update_Should_PreserveExistingValues_WhenNullIsPassed()
    {
        // Arrange
        var settings = Domain.Users.UserSettings.Create(Guid.NewGuid());
        settings.Update("en", true, "UTC");

        // Act
        var result = settings.Update(null, null, null);

        // Assert
        result.IsSuccess.Should().BeTrue();
        settings.PreferredLanguage.Should().Be("en");
        settings.EmailNotificationsEnabled.Should().BeTrue();
        settings.Timezone.Should().Be("UTC");
    }

    [Fact]
    public void Update_Should_OnlyUpdateProvidedFields()
    {
        // Arrange
        var settings = Domain.Users.UserSettings.Create(Guid.NewGuid());
        settings.Update("en", true, "UTC");

        // Act
        var result = settings.Update("fr", null, null);

        // Assert
        result.IsSuccess.Should().BeTrue();
        settings.PreferredLanguage.Should().Be("fr");
        settings.EmailNotificationsEnabled.Should().BeTrue();
        settings.Timezone.Should().Be("UTC");
    }

    [Fact]
    public void Update_Should_UpdateAllFields_WhenAllProvided()
    {
        // Arrange
        var settings = Domain.Users.UserSettings.Create(Guid.NewGuid());
        settings.Update("en", true, "UTC");

        // Act
        var result = settings.Update("de", false, "CET");

        // Assert
        result.IsSuccess.Should().BeTrue();
        settings.PreferredLanguage.Should().Be("de");
        settings.EmailNotificationsEnabled.Should().BeFalse();
        settings.Timezone.Should().Be("CET");
    }
}
