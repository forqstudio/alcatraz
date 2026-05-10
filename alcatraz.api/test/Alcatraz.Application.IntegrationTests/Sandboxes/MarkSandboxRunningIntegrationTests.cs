using FluentAssertions;
using Alcatraz.Application.IntegrationTests.Infrastructure;
using Alcatraz.Application.Sandboxes.MarkSandboxRunning;
using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Sandboxes.Events;
using Alcatraz.Domain.Users;
using Alcatraz.Infrastructure.Outbox;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Alcatraz.Application.IntegrationTests.Sandboxes;

public class MarkSandboxRunningIntegrationTests : BaseIntegrationTest
{
    private readonly IntegrationTestWebAppFactory _factory;

    public MarkSandboxRunningIntegrationTests(IntegrationTestWebAppFactory factory)
        : base(factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Handle_PersistsAllRuntimeFields_AndWritesOutboxMessage()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Alcatraz.Infrastructure.ApplicationDbContext>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var user = User.Create(
            new FirstName("test"),
            new LastName("user"),
            new Email($"sandbox-mark-running+{Guid.NewGuid():N}@test.com"));
        user.SetIdentityId(Guid.NewGuid().ToString());
        userRepo.Add(user);
        await dbContext.SaveChangesAsync();

        var sandbox = Sandbox.Request(user.Id, vcpus: 2, memoryMib: 2048, DateTime.UtcNow);
        dbContext.Set<Sandbox>().Add(sandbox);
        await dbContext.SaveChangesAsync();

        var readyAt = DateTime.UtcNow;
        var runtime = new SandboxRuntimeInfo(
            Host: "10.0.0.42",
            Port: 22,
            ActualVcpus: 2,
            ActualMemoryMib: 2048,
            BootDurationMs: 4321,
            ReadyAtUtc: readyAt,
            VmmVersion: "v1.6.0",
            VmmState: "Running",
            FirecrackerPid: 4732,
            SocketPath: "/run/firecracker/sb-it.sock",
            TapDevice: "tap-it",
            MacAddress: "02:fc:00:00:00:42",
            VmIp: "10.0.0.42",
            HostGatewayIp: "10.0.0.1",
            NfsPort: 2049,
            WorkerSlotIndex: 7,
            RootfsPath: "/var/lib/alcatraz/rootfs/it.ext4",
            KernelPath: "/var/lib/alcatraz/kernels/it.bin");

        var result = await sender.Send(new MarkSandboxRunningCommand(sandbox.Id, runtime));
        result.IsSuccess.Should().BeTrue();

        dbContext.ChangeTracker.Clear();
        var stored = await dbContext.Set<Sandbox>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == sandbox.Id);
        stored.Should().NotBeNull();
        stored!.Status.Should().Be(SandboxStatus.Running);
        stored.Host.Should().Be(runtime.Host);
        stored.Port.Should().Be(runtime.Port);
        stored.ActualVcpus.Should().Be(runtime.ActualVcpus);
        stored.ActualMemoryMib.Should().Be(runtime.ActualMemoryMib);
        stored.BootDurationMs.Should().Be(runtime.BootDurationMs);
        stored.ReadyAtUtc.Should().NotBeNull();
        stored.VmmVersion.Should().Be(runtime.VmmVersion);
        stored.VmmState.Should().Be(runtime.VmmState);
        stored.FirecrackerPid.Should().Be(runtime.FirecrackerPid);
        stored.SocketPath.Should().Be(runtime.SocketPath);
        stored.TapDevice.Should().Be(runtime.TapDevice);
        stored.MacAddress.Should().Be(runtime.MacAddress);
        stored.VmIp.Should().Be(runtime.VmIp);
        stored.HostGatewayIp.Should().Be(runtime.HostGatewayIp);
        stored.NfsPort.Should().Be(runtime.NfsPort);
        stored.WorkerSlotIndex.Should().Be(runtime.WorkerSlotIndex);
        stored.RootfsPath.Should().Be(runtime.RootfsPath);
        stored.KernelPath.Should().Be(runtime.KernelPath);

        var outboxMessages = await dbContext.Set<OutboxMessage>()
            .Where(m => m.Type == nameof(SandboxBecameRunningDomainEvent))
            .ToListAsync();
        outboxMessages.Should().Contain(m => m.Content.Contains(sandbox.Id.ToString()));
    }
}
