using Alcatraz.Cli.Commands.Login;
using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Authentication;
using FluentAssertions;
using NSubstitute;

namespace Alcatraz.Cli.UnitTests.Commands.Login;

[Collection("ConfigPath")]
public class DeviceFlowOrchestratorTests
{
    private readonly IAlcatrazApiClient api = Substitute.For<IAlcatrazApiClient>();
    private readonly ITokenStore tokens = Substitute.For<ITokenStore>();
    private readonly FakeClock clock = new(new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc));

    private DeviceAuthorizationResponse Init() => new(
        DeviceCode: "DEV", UserCode: "ABCD",
        VerificationUri: "https://idp/d",
        VerificationUriComplete: "https://idp/d?u=ABCD",
        ExpiresIn: 600, Interval: 5);

    [Fact]
    public async Task Run_HappyPath_PersistsTokens()
    {
        api.PollDeviceTokenAsync("DEV", Arg.Any<CancellationToken>())
            .Returns(new DeviceTokenExchangeResult(
                new DeviceTokenResponse("at", "rt", 300, "Bearer", null),
                DeviceTokenError.None,
                null));

        var orch = new DeviceFlowOrchestrator(api, tokens, clock);

        var stored = await orch.RunAsync(Init(), CancellationToken.None);

        stored.AccessToken.Should().Be("at");
        stored.RefreshToken.Should().Be("rt");
        await tokens.Received().SaveAsync(
            Arg.Is<StoredTokens>(s => s.AccessToken == "at"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_AccessDenied_Throws()
    {
        api.PollDeviceTokenAsync("DEV", Arg.Any<CancellationToken>())
            .Returns(new DeviceTokenExchangeResult(null, DeviceTokenError.AccessDenied, "access_denied"));

        var orch = new DeviceFlowOrchestrator(api, tokens, clock);

        var act = () => orch.RunAsync(Init(), CancellationToken.None);
        await act.Should().ThrowAsync<AuthorizationDeniedException>();
    }

    [Fact]
    public async Task Run_ExpiredToken_Throws()
    {
        api.PollDeviceTokenAsync("DEV", Arg.Any<CancellationToken>())
            .Returns(new DeviceTokenExchangeResult(null, DeviceTokenError.ExpiredToken, "expired_token"));

        var orch = new DeviceFlowOrchestrator(api, tokens, clock);

        var act = () => orch.RunAsync(Init(), CancellationToken.None);
        await act.Should().ThrowAsync<ExpiredDeviceCodeException>();
    }

    private sealed class FakeClock(DateTime initial) : TimeProvider
    {
        private DateTime utcNow = initial;
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);

        // Resolve Task.Delay(TimeSpan, TimeProvider, CancellationToken) by advancing
        // the clock immediately and returning a completed task.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            utcNow = utcNow.Add(dueTime);
            callback(state);
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
