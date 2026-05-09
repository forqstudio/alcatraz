using FluentAssertions;
using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Auth.ExchangeDeviceToken;
using Alcatraz.Domain.Abstractions;
using NSubstitute;

namespace Alcatraz.Application.UnitTests.Auth;

public class ExchangeDeviceTokenCommandHandlerTests
{
    private readonly IDeviceAuthorizationClient _client = Substitute.For<IDeviceAuthorizationClient>();

    [Fact]
    public async Task Handle_OnSuccess_ReturnsToken()
    {
        var token = new DeviceTokenResponse("access", "refresh", 300, "Bearer", null);
        _client.ExchangeAsync("dc", Arg.Any<CancellationToken>())
            .Returns(Result.Success(token));

        var handler = new ExchangeDeviceTokenCommandHandler(_client);

        var result = await handler.Handle(new ExchangeDeviceTokenCommand("dc"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AccessToken.Should().Be("access");
    }

    [Theory]
    [MemberData(nameof(PendingErrors))]
    public async Task Handle_PropagatesPollingErrors(Error expected)
    {
        _client.ExchangeAsync("dc", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DeviceTokenResponse>(expected));

        var handler = new ExchangeDeviceTokenCommandHandler(_client);

        var result = await handler.Handle(new ExchangeDeviceTokenCommand("dc"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(expected);
    }

    public static IEnumerable<object[]> PendingErrors() => new[]
    {
        new object[] { DeviceAuthErrors.AuthorizationPending },
        new object[] { DeviceAuthErrors.SlowDown },
        new object[] { DeviceAuthErrors.ExpiredToken },
        new object[] { DeviceAuthErrors.AccessDenied },
    };
}
