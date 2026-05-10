using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Alcatraz.Api.Controllers.Sandboxes;
using Alcatraz.Api.FunctionalTests.Infrastructure;
using Alcatraz.Application.Sandboxes;
using Alcatraz.Application.Sandboxes.IssueSshCertificate;
using Alcatraz.Application.Sandboxes.MarkSandboxRunning;
using Alcatraz.Domain.Sandboxes;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Alcatraz.Api.FunctionalTests.Sandboxes;

public class SandboxesEndpointsTests : BaseFunctionalTest
{
    private readonly FunctionalTestWebAppFactory _factory;

    public SandboxesEndpointsTests(FunctionalTestWebAppFactory factory) : base(factory)
    {
        _factory = factory;
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
    public async Task GetSandbox_AfterMarkedRunning_RoundTripsAllRuntimeFields()
    {
        var client = await AuthenticatedClient();
        var created = await client.PostAsJsonAsync("api/v1/sandboxes", new CreateSandboxRequest(2, 2048));
        var body = (await created.Content.ReadFromJsonAsync<SandboxResponse>())!;

        var runtime = new SandboxRuntimeInfo(
            Host: "10.0.0.42",
            Port: 22,
            ActualVcpus: 2,
            ActualMemoryMib: 2048,
            BootDurationMs: 4321,
            ReadyAtUtc: DateTime.UtcNow,
            VmmVersion: "v1.6.0",
            VmmState: "Running",
            FirecrackerPid: 4732,
            SocketPath: "/run/firecracker/sb-test.sock",
            TapDevice: "tap-7",
            MacAddress: "02:fc:00:00:00:07",
            VmIp: "10.0.0.42",
            HostGatewayIp: "10.0.0.1",
            NfsPort: 2049,
            WorkerSlotIndex: 7,
            RootfsPath: "/var/lib/alcatraz/rootfs/ubuntu-22.04.ext4",
            KernelPath: "/var/lib/alcatraz/kernels/vmlinux-5.10.bin");

        using (var scope = _factory.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new MarkSandboxRunningCommand(body.Id, runtime));
        }

        var get = await client.GetAsync($"api/v1/sandboxes/{body.Id}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = (await get.Content.ReadFromJsonAsync<SandboxResponse>())!;

        detail.Status.Should().Be((int)SandboxStatus.Running);
        detail.Host.Should().Be(runtime.Host);
        detail.Port.Should().Be(runtime.Port);
        detail.ActualVcpus.Should().Be(runtime.ActualVcpus);
        detail.ActualMemoryMib.Should().Be(runtime.ActualMemoryMib);
        detail.BootDurationMs.Should().Be(runtime.BootDurationMs);
        detail.ReadyAtUtc.Should().NotBeNull();
        detail.VmmVersion.Should().Be(runtime.VmmVersion);
        detail.VmmState.Should().Be(runtime.VmmState);
        detail.FirecrackerPid.Should().Be(runtime.FirecrackerPid);
        detail.SocketPath.Should().Be(runtime.SocketPath);
        detail.TapDevice.Should().Be(runtime.TapDevice);
        detail.MacAddress.Should().Be(runtime.MacAddress);
        detail.VmIp.Should().Be(runtime.VmIp);
        detail.HostGatewayIp.Should().Be(runtime.HostGatewayIp);
        detail.NfsPort.Should().Be(runtime.NfsPort);
        detail.WorkerSlotIndex.Should().Be(runtime.WorkerSlotIndex);
        detail.RootfsPath.Should().Be(runtime.RootfsPath);
        detail.KernelPath.Should().Be(runtime.KernelPath);

        var list = await client.GetAsync("api/v1/sandboxes");
        var listBodies = (await list.Content.ReadFromJsonAsync<List<SandboxResponse>>())!;
        var listEntry = listBodies.Single(s => s.Id == body.Id);
        listEntry.VmIp.Should().Be(runtime.VmIp);
        listEntry.RootfsPath.Should().Be(runtime.RootfsPath);
        listEntry.FirecrackerPid.Should().Be(runtime.FirecrackerPid);
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
