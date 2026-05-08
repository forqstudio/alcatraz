using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.UserSettings.GetUserSettings;
using ForqStudio.Domain.Users;
using FluentAssertions;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.UserSettings;

public class GetUserSettingsTests
{
    private readonly IUserSettingsRepository _userSettingsRepositoryMock;
    private readonly IUserContext _userContextMock;
    private readonly GetUserSettingsQueryHandler _handler;

    public GetUserSettingsTests()
    {
        _userSettingsRepositoryMock = Substitute.For<IUserSettingsRepository>();
        _userContextMock = Substitute.For<IUserContext>();

        _handler = new GetUserSettingsQueryHandler(
            _userSettingsRepositoryMock,
            _userContextMock);
    }

    [Fact]
    public async Task Handle_Should_ReturnDefaultResponse_WhenSettingsDoNotExist()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _userContextMock.UserId.Returns(userId);

        _userSettingsRepositoryMock
            .GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Domain.Users.UserSettings?)null);

        var query = new GetUserSettingsQuery();

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(Guid.Empty);
        result.Value.UserId.Should().Be(userId);
        result.Value.PreferredLanguage.Should().BeNull();
        result.Value.EmailNotificationsEnabled.Should().BeNull();
        result.Value.Timezone.Should().BeNull();
    }

    [Fact]
    public async Task Handle_Should_NotCallAdd_WhenSettingsDoNotExist()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _userContextMock.UserId.Returns(userId);

        _userSettingsRepositoryMock
            .GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Domain.Users.UserSettings?)null);

        var query = new GetUserSettingsQuery();

        // Act
        await _handler.Handle(query, default);

        // Assert
        _userSettingsRepositoryMock.DidNotReceive().Add(Arg.Any<Domain.Users.UserSettings>());
    }

    [Fact]
    public async Task Handle_Should_ReturnExistingSettings_WhenSettingsExist()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _userContextMock.UserId.Returns(userId);

        var settings = Domain.Users.UserSettings.Create(userId);
        settings.Update("en", true, "UTC");

        _userSettingsRepositoryMock
            .GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(settings);

        var query = new GetUserSettingsQuery();

        // Act
        var result = await _handler.Handle(query, default);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(userId);
        result.Value.PreferredLanguage.Should().Be("en");
        result.Value.EmailNotificationsEnabled.Should().BeTrue();
        result.Value.Timezone.Should().Be("UTC");
    }
}
