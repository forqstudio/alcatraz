using FluentAssertions;
using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Auth.RefreshDeviceToken;
using ForqStudio.Domain.Abstractions;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Auth;

public class RefreshDeviceTokenCommandHandlerTests
{
    private readonly IDeviceAuthorizationClient _client = Substitute.For<IDeviceAuthorizationClient>();

    [Fact]
    public async Task Handle_OnSuccess_ReturnsRefreshedToken()
    {
        var token = new DeviceTokenResponse("new-access", "new-refresh", 300, "Bearer", null);
        _client.RefreshAsync("rt", Arg.Any<CancellationToken>())
            .Returns(Result.Success(token));

        var handler = new RefreshDeviceTokenCommandHandler(_client);

        var result = await handler.Handle(new RefreshDeviceTokenCommand("rt"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("new-access");
        result.Value.RefreshToken.Should().Be("new-refresh");
    }

    [Theory]
    [MemberData(nameof(RefreshErrors))]
    public async Task Handle_PropagatesRefreshErrors(Error expected)
    {
        _client.RefreshAsync("rt", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DeviceTokenResponse>(expected));

        var handler = new RefreshDeviceTokenCommandHandler(_client);

        var result = await handler.Handle(new RefreshDeviceTokenCommand("rt"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(expected);
    }

    public static IEnumerable<object[]> RefreshErrors() => new[]
    {
        new object[] { DeviceAuthErrors.InvalidGrant },
        new object[] { DeviceAuthErrors.RefreshFailed },
    };
}
