using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using ForqStudio.Api.Controllers.Auth;
using ForqStudio.Api.FunctionalTests.Infrastructure;
using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Domain.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace ForqStudio.Api.FunctionalTests.Auth;

public class DeviceFlowEndpointsTests : IClassFixture<DeviceFlowEndpointsTests.Factory>
{
    private readonly Factory _factory;

    public DeviceFlowEndpointsTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task InitiateDevice_ReturnsDeviceCodeFields()
    {
        _factory.Client.InitiateAsync(Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DeviceAuthorizationResponse(
                DeviceCode: "DEV-CODE",
                UserCode: "ABCD-EFGH",
                VerificationUri: "https://idp/device",
                VerificationUriComplete: "https://idp/device?user_code=ABCD-EFGH",
                ExpiresIn: 600,
                Interval: 5)));

        var http = _factory.CreateClient();
        var response = await http.PostAsync("api/v1/auth/device", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DeviceAuthorizationResponse>();
        body.Should().NotBeNull();
        body!.DeviceCode.Should().Be("DEV-CODE");
        body.UserCode.Should().Be("ABCD-EFGH");
        body.Interval.Should().Be(5);
    }

    [Fact]
    public async Task ExchangeDeviceToken_HappyPath_ReturnsToken()
    {
        _factory.Client.ExchangeAsync("DEV-CODE", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DeviceTokenResponse(
                AccessToken: "at",
                RefreshToken: "rt",
                ExpiresIn: 300,
                TokenType: "Bearer",
                IdToken: null)));

        var http = _factory.CreateClient();
        var response = await http.PostAsJsonAsync(
            "api/v1/auth/device/token",
            new ExchangeDeviceTokenRequest("DEV-CODE"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DeviceTokenResponse>();
        body!.AccessToken.Should().Be("at");
    }

    [Theory]
    [InlineData("authorization_pending")]
    [InlineData("slow_down")]
    [InlineData("expired_token")]
    [InlineData("access_denied")]
    public async Task ExchangeDeviceToken_PollingError_Returns400_WithErrorExtension(string keycloakError)
    {
        var error = keycloakError switch
        {
            "authorization_pending" => DeviceAuthErrors.AuthorizationPending,
            "slow_down" => DeviceAuthErrors.SlowDown,
            "expired_token" => DeviceAuthErrors.ExpiredToken,
            "access_denied" => DeviceAuthErrors.AccessDenied,
            _ => DeviceAuthErrors.ExchangeFailed,
        };

        _factory.Client.ExchangeAsync("DEV-CODE", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DeviceTokenResponse>(error));

        var http = _factory.CreateClient();
        var response = await http.PostAsJsonAsync(
            "api/v1/auth/device/token",
            new ExchangeDeviceTokenRequest("DEV-CODE"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        problem.GetProperty("error").GetString().Should().Be(keycloakError);
    }

    [Fact]
    public async Task ExchangeDeviceToken_MissingCode_Returns400()
    {
        var http = _factory.CreateClient();
        var response = await http.PostAsJsonAsync(
            "api/v1/auth/device/token",
            new ExchangeDeviceTokenRequest(""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefreshDeviceToken_HappyPath_ReturnsToken()
    {
        _factory.Client.RefreshAsync("rt", Arg.Any<CancellationToken>())
            .Returns(Result.Success(new DeviceTokenResponse(
                AccessToken: "fresh-at",
                RefreshToken: "rotated-rt",
                ExpiresIn: 300,
                TokenType: "Bearer",
                IdToken: null)));

        var http = _factory.CreateClient();
        var response = await http.PostAsJsonAsync(
            "api/v1/auth/refresh",
            new RefreshDeviceTokenRequest("rt"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DeviceTokenResponse>();
        body!.AccessToken.Should().Be("fresh-at");
        body.RefreshToken.Should().Be("rotated-rt");
    }

    [Fact]
    public async Task RefreshDeviceToken_InvalidGrant_Returns400_WithErrorExtension()
    {
        _factory.Client.RefreshAsync("rt", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DeviceTokenResponse>(DeviceAuthErrors.InvalidGrant));

        var http = _factory.CreateClient();
        var response = await http.PostAsJsonAsync(
            "api/v1/auth/refresh",
            new RefreshDeviceTokenRequest("rt"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        problem.GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Fact]
    public async Task RefreshDeviceToken_MissingToken_Returns400()
    {
        var http = _factory.CreateClient();
        var response = await http.PostAsJsonAsync(
            "api/v1/auth/refresh",
            new RefreshDeviceTokenRequest(""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public IDeviceAuthorizationClient Client { get; } = Substitute.For<IDeviceAuthorizationClient>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Run as Production so Program.cs skips ApplyMigrations/SeedData and
            // doesn't need a real Postgres for these auth-only tests.
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Database", "Host=localhost;Port=1;Database=ignored;Username=ignored;Password=ignored;");
            builder.UseSetting("ConnectionStrings:Cache", "localhost:1");
            builder.UseSetting("KeyCloak:BaseUrl", "http://localhost:1");
            builder.UseSetting("Authentication:Audience", "test");
            builder.UseSetting("Authentication:ValidIssuer", "http://localhost:1/realms/test");
            builder.UseSetting("Authentication:MetadataUrl", "http://localhost:1/realms/test/.well-known/openid-configuration");
            builder.UseSetting("Authentication:RequireHttpsMetadata", "false");
            builder.UseSetting("Outbox:IntervalInSeconds", "60");
            builder.UseSetting("Outbox:BatchSize", "10");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDeviceAuthorizationClient>();
                services.AddSingleton(Client);
            });
        }
    }
}
