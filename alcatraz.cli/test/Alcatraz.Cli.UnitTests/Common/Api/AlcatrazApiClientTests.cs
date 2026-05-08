using System.Net;
using Alcatraz.Cli.Commands.Login;
using Alcatraz.Cli.Common.Api;
using FluentAssertions;
using RichardSzalay.MockHttp;

namespace Alcatraz.Cli.UnitTests.Common.Api;

public class AlcatrazApiClientTests
{
    private static (AlcatrazApiClient Client, MockHttpMessageHandler Mock) BuildClient()
    {
        var mock = new MockHttpMessageHandler();
        var http = mock.ToHttpClient();
        http.BaseAddress = new Uri("http://api/");
        return (new AlcatrazApiClient(http), mock);
    }

    [Fact]
    public async Task PollDeviceTokenAsync_AuthorizationPending_ReturnsResultNoThrow()
    {
        var (client, mock) = BuildClient();
        mock.When(HttpMethod.Post, "http://api/api/v1/auth/device/token")
            .Respond(HttpStatusCode.BadRequest, "application/json",
                """{"title":"x","status":400,"detail":"x","error":"authorization_pending"}""");

        var result = await client.PollDeviceTokenAsync("dc");

        result.Token.Should().BeNull();
        result.ErrorKind.Should().Be(DeviceTokenError.AuthorizationPending);
    }

    [Fact]
    public async Task PollDeviceTokenAsync_HappyPath_ReturnsToken()
    {
        var (client, mock) = BuildClient();
        mock.When(HttpMethod.Post, "http://api/api/v1/auth/device/token")
            .Respond("application/json",
                """{"accessToken":"at","refreshToken":"rt","expiresIn":300,"tokenType":"Bearer","idToken":null}""");

        var result = await client.PollDeviceTokenAsync("dc");

        result.ErrorKind.Should().Be(DeviceTokenError.None);
        result.Token!.AccessToken.Should().Be("at");
    }

    [Fact]
    public async Task RefreshDeviceTokenAsync_InvalidGrant_ThrowsNotLoggedIn()
    {
        var (client, mock) = BuildClient();
        mock.When(HttpMethod.Post, "http://api/api/v1/auth/refresh")
            .Respond(HttpStatusCode.BadRequest, "application/json",
                """{"title":"x","status":400,"detail":"x","error":"invalid_grant"}""");

        var act = () => client.RefreshDeviceTokenAsync("rt");

        await act.Should().ThrowAsync<NotLoggedInException>();
    }

    [Fact]
    public async Task GetSandboxAsync_NotFound_ThrowsSandboxNotFound()
    {
        var (client, mock) = BuildClient();
        var id = Guid.NewGuid();
        mock.When(HttpMethod.Get, $"http://api/api/v1/sandboxes/{id}")
            .Respond(HttpStatusCode.NotFound);

        var act = () => client.GetSandboxAsync(id);

        await act.Should().ThrowAsync<SandboxNotFoundException>()
            .Where(ex => ex.SandboxId == id);
    }

    [Fact]
    public async Task IssueSshCertificateAsync_Happy_ReturnsCert()
    {
        var (client, mock) = BuildClient();
        var id = Guid.NewGuid();
        mock.When(HttpMethod.Post, $"http://api/api/v1/sandboxes/{id}/ssh-cert")
            .Respond("application/json",
                """{"cert":"ssh-cert-body","validUntilUtc":"2030-01-02T12:00:00Z","gatewayHost":"localhost","gatewayPort":2222}""");

        var result = await client.IssueSshCertificateAsync(id, "ssh-ed25519 AAAAA...");

        result.Cert.Should().Be("ssh-cert-body");
        result.GatewayHost.Should().Be("localhost");
        result.GatewayPort.Should().Be(2222);
    }
}
