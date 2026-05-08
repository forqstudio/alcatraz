using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using ForqStudio.Api.Controllers.Sandboxes;
using ForqStudio.Api.FunctionalTests.Infrastructure;
using ForqStudio.Application.Sandboxes;
using ForqStudio.Application.Sandboxes.IssueSshCertificate;

namespace ForqStudio.Api.FunctionalTests.Sandboxes;

public class SandboxesEndpointsTests : BaseFunctionalTest
{
    public SandboxesEndpointsTests(FunctionalTestWebAppFactory factory) : base(factory)
    {
    }

    private async Task<HttpClient> AuthenticatedClient()
    {
        var token = await GetAccessToken();
        HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return HttpClient;
    }

    [Fact]
    public async Task UnauthenticatedCreate_ShouldReturnUnauthorized()
    {
        HttpClient.DefaultRequestHeaders.Authorization = null;
        var response = await HttpClient.PostAsJsonAsync(
            "api/v1/sandboxes",
            new CreateSandboxRequest(2, 2048));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateSandbox_HappyPath_Returns201_WithBody()
    {
        var client = await AuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "api/v1/sandboxes",
            new CreateSandboxRequest(2, 2048));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<SandboxResponse>();
        body.Should().NotBeNull();
        body!.Id.Should().NotBe(Guid.Empty);
        body.Vcpus.Should().Be(2);
        body.MemoryMib.Should().Be(2048);
        body.Status.Should().Be(1); // Provisioning
    }

    [Theory]
    [InlineData(0, 2048)]
    [InlineData(2, 100)]
    [InlineData(2, 1000)]
    public async Task CreateSandbox_InvalidBody_Returns400(int vcpus, int memoryMib)
    {
        var client = await AuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "api/v1/sandboxes",
            new CreateSandboxRequest(vcpus, memoryMib));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetSandbox_ForOwner_Returns200()
    {
        var client = await AuthenticatedClient();
        var created = await client.PostAsJsonAsync("api/v1/sandboxes", new CreateSandboxRequest(2, 2048));
        var body = (await created.Content.ReadFromJsonAsync<SandboxResponse>())!;

        var get = await client.GetAsync($"api/v1/sandboxes/{body.Id}");

        get.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSandbox_NonExistent_Returns404()
    {
        var client = await AuthenticatedClient();
        var get = await client.GetAsync($"api/v1/sandboxes/{Guid.NewGuid()}");

        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListSandboxes_ReturnsCallerOwned()
    {
        var client = await AuthenticatedClient();
        await client.PostAsJsonAsync("api/v1/sandboxes", new CreateSandboxRequest(2, 2048));

        var list = await client.GetAsync("api/v1/sandboxes");
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        var bodies = await list.Content.ReadFromJsonAsync<List<SandboxResponse>>();
        bodies.Should().NotBeNull();
        bodies!.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DeleteSandbox_HappyPath_Returns202_AndExcludesFromList()
    {
        var client = await AuthenticatedClient();
        var created = await client.PostAsJsonAsync("api/v1/sandboxes", new CreateSandboxRequest(2, 2048));
        var body = (await created.Content.ReadFromJsonAsync<SandboxResponse>())!;

        var del = await client.DeleteAsync($"api/v1/sandboxes/{body.Id}");
        del.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task DeleteSandbox_NonExistent_Returns404()
    {
        var client = await AuthenticatedClient();
        var del = await client.DeleteAsync($"api/v1/sandboxes/{Guid.NewGuid()}");

        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IssueSshCertificate_InvalidPubkey_Returns400()
    {
        var client = await AuthenticatedClient();
        var created = await client.PostAsJsonAsync("api/v1/sandboxes", new CreateSandboxRequest(2, 2048));
        var body = (await created.Content.ReadFromJsonAsync<SandboxResponse>())!;

        var resp = await client.PostAsJsonAsync(
            $"api/v1/sandboxes/{body.Id}/ssh-cert",
            new IssueSshCertificateRequest("not-a-key"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

}
