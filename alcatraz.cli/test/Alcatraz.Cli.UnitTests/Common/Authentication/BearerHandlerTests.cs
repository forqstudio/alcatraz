using System.Net;
using Alcatraz.Cli.Commands.Login;
using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Authentication;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RichardSzalay.MockHttp;

namespace Alcatraz.Cli.UnitTests.Common.Authentication;

public class BearerHandlerTests
{
    private readonly InMemoryTokenStore tokens = new();
    private readonly IAlcatrazApiClient api = Substitute.For<IAlcatrazApiClient>();
    private readonly FakeClock clock = new(new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task SendAsync_FreshToken_AttachesBearer()
    {
        await tokens.SaveAsync(
            new StoredTokens("fresh-at", "rt", clock.UtcNow.AddMinutes(10), "Bearer", null));

        var (handler, mock) = BuildHandler();
        mock.Expect("http://api/x")
            .WithHeaders("Authorization", "Bearer fresh-at")
            .Respond("text/plain", "ok");

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api/") };
        var resp = await http.GetAsync("x");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        mock.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task SendAsync_NearExpiry_RefreshesBeforeAttaching()
    {
        await tokens.SaveAsync(
            new StoredTokens("stale-at", "rt", clock.UtcNow.AddSeconds(30), "Bearer", null));
        api.RefreshDeviceTokenAsync("rt", Arg.Any<CancellationToken>())
            .Returns(new DeviceTokenResponse("new-at", "new-rt", 300, "Bearer", null));

        var (handler, mock) = BuildHandler();
        mock.Expect("http://api/x")
            .WithHeaders("Authorization", "Bearer new-at")
            .Respond("text/plain", "ok");

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api/") };
        var resp = await http.GetAsync("x");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        await api.Received(1).RefreshDeviceTokenAsync("rt", Arg.Any<CancellationToken>());
        mock.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task SendAsync_401_RefreshesAndRetries()
    {
        await tokens.SaveAsync(
            new StoredTokens("stale-at", "rt", clock.UtcNow.AddMinutes(10), "Bearer", null));
        api.RefreshDeviceTokenAsync("rt", Arg.Any<CancellationToken>())
            .Returns(new DeviceTokenResponse("retry-at", "rt", 300, "Bearer", null));

        var (handler, mock) = BuildHandler();
        mock.Expect(HttpMethod.Get, "http://api/x")
            .WithHeaders("Authorization", "Bearer stale-at")
            .Respond(HttpStatusCode.Unauthorized);
        mock.Expect(HttpMethod.Get, "http://api/x")
            .WithHeaders("Authorization", "Bearer retry-at")
            .Respond("text/plain", "ok");

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api/") };
        var resp = await http.GetAsync("x");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        mock.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task SendAsync_AnonRequest_SkipsBearer()
    {
        var (handler, mock) = BuildHandler();
        mock.Expect("http://api/anon")
            .With(req => !req.Headers.Contains("Authorization"))
            .Respond("text/plain", "ok");

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api/") };
        using var request = new HttpRequestMessage(HttpMethod.Post, "anon");
        request.Options.Set(AlcatrazApiClient.AnonRequestOption, true);

        var resp = await http.SendAsync(request);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        tokens.LoadCount.Should().Be(0);
        mock.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task SendAsync_NoStoredTokens_Throws()
    {
        var (handler, _) = BuildHandler();

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://api/") };
        var act = () => http.GetAsync("x");

        await act.Should().ThrowAsync<NotLoggedInException>();
    }

    private (BearerHandler Handler, MockHttpMessageHandler Mock) BuildHandler()
    {
        var mock = new MockHttpMessageHandler();
        var services = new ServiceCollection();
        services.AddSingleton(api);
        var sp = services.BuildServiceProvider();

        var handler = new BearerHandler(tokens, sp, clock, NullLogger<BearerHandler>.Instance)
        {
            InnerHandler = mock,
        };
        return (handler, mock);
    }

    private sealed class FakeClock(DateTime initial) : TimeProvider
    {
        public DateTime UtcNow { get; } = initial;
        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }

    private sealed class InMemoryTokenStore : ITokenStore
    {
        private StoredTokens? current;
        public int LoadCount { get; private set; }

        public Task<StoredTokens?> LoadAsync(CancellationToken ct = default)
        {
            LoadCount++;
            return Task.FromResult(current);
        }

        public Task SaveAsync(StoredTokens tokens, CancellationToken ct = default)
        {
            current = tokens;
            return Task.CompletedTask;
        }

        public void Clear() => current = null;
    }
}
