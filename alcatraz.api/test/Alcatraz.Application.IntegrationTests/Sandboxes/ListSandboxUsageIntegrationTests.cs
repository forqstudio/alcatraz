using FluentAssertions;
using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Data;
using Alcatraz.Application.IntegrationTests.Infrastructure;
using Alcatraz.Application.Sandboxes.Usage;
using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Sandboxes.Usage;
using Alcatraz.Domain.Users;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Alcatraz.Application.IntegrationTests.Sandboxes;

public class ListSandboxUsageIntegrationTests : BaseIntegrationTest
{
    private static readonly DateTime BootedAt = new(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);

    private readonly IntegrationTestWebAppFactory _factory;

    public ListSandboxUsageIntegrationTests(IntegrationTestWebAppFactory factory)
        : base(factory)
    {
        _factory = factory;
    }

    private static SandboxRuntimeInfo Runtime(int vcpus = 4, int memoryMib = 8192) =>
        new(
            Host: "172.16.0.10",
            Port: 22,
            ActualVcpus: vcpus,
            ActualMemoryMib: memoryMib,
            BootDurationMs: 1234,
            ReadyAtUtc: BootedAt,
            VmmVersion: "1.15.1",
            VmmState: "Running",
            FirecrackerPid: 4242,
            SocketPath: "/tmp/alcatraz-test.sock",
            TapDevice: "fc-tap0",
            MacAddress: "AA:FC:00:00:00:01",
            VmIp: "172.16.0.10",
            HostGatewayIp: "172.16.0.1",
            NfsPort: 8000,
            WorkerSlotIndex: 0,
            RootfsPath: "/test/rootfs",
            KernelPath: "/test/vmlinux");

    private static User MakeUser(string label) =>
        User.Create(
            new FirstName(label),
            new LastName("user"),
            new Email($"sandbox-usage-list-{label}+{Guid.NewGuid():N}@test.com"));

    private static Sandbox RunningSandbox(Guid ownerId, DateTime readyAt)
    {
        var sandbox = Sandbox.Request(ownerId, 4, 8192, readyAt);
        sandbox.MarkRunning(Runtime() with { ReadyAtUtc = readyAt }, readyAt);
        return sandbox;
    }

    private static SandboxUsageRecord FinaliseFor(Sandbox sandbox, DateTime readyAt, DateTime windowEnd)
    {
        sandbox.MarkDeleting(windowEnd);
        var final = new SandboxUsageFinal(
            VmBootedAtUtc: readyAt,
            FinalisedAtUtc: windowEnd,
            TotalCpuUsageUsec: 5_000_000,
            TotalNetRxBytes: 1_500_000,
            TotalNetTxBytes: 1_800_000,
            SampleCount: 5);
        return SandboxUsageRecord.Finalise(sandbox, final, windowEnd).Value;
    }

    [Fact]
    public async Task Handle_ReturnsOnlyCallerOwnedRecords()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Alcatraz.Infrastructure.ApplicationDbContext>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var sqlFactory = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        var caller = MakeUser("caller");
        var other = MakeUser("other");
        caller.SetIdentityId(Guid.NewGuid().ToString());
        other.SetIdentityId(Guid.NewGuid().ToString());
        userRepo.Add(caller);
        userRepo.Add(other);
        await dbContext.SaveChangesAsync();

        var mine = RunningSandbox(caller.Id, BootedAt);
        var theirs = RunningSandbox(other.Id, BootedAt);
        dbContext.Set<Sandbox>().AddRange(mine, theirs);
        await dbContext.SaveChangesAsync();

        dbContext.Set<SandboxUsageRecord>().AddRange(
            FinaliseFor(mine, BootedAt, BootedAt.AddMinutes(5)),
            FinaliseFor(theirs, BootedAt, BootedAt.AddMinutes(5)));
        await dbContext.SaveChangesAsync();

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(caller.Id);
        var handler = new ListSandboxUsageQueryHandler(sqlFactory, userContext);

        var result = await handler.Handle(new ListSandboxUsageQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(u => u.SandboxId).Should().Contain(mine.Id);
        result.Value.Select(u => u.SandboxId).Should().NotContain(theirs.Id);
    }

    [Fact]
    public async Task Handle_OrdersByBillingWindowEndUtc_Descending()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Alcatraz.Infrastructure.ApplicationDbContext>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var sqlFactory = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        var user = MakeUser("orders");
        user.SetIdentityId(Guid.NewGuid().ToString());
        userRepo.Add(user);
        await dbContext.SaveChangesAsync();

        var t0 = new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc);
        var oldest = RunningSandbox(user.Id, t0);
        var middle = RunningSandbox(user.Id, t0.AddHours(1));
        var newest = RunningSandbox(user.Id, t0.AddHours(2));
        dbContext.Set<Sandbox>().AddRange(oldest, middle, newest);
        await dbContext.SaveChangesAsync();

        dbContext.Set<SandboxUsageRecord>().AddRange(
            FinaliseFor(oldest, t0,            t0.AddHours(1)),
            FinaliseFor(middle, t0.AddHours(1), t0.AddHours(2)),
            FinaliseFor(newest, t0.AddHours(2), t0.AddHours(3)));
        await dbContext.SaveChangesAsync();

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(user.Id);
        var handler = new ListSandboxUsageQueryHandler(sqlFactory, userContext);

        var result = await handler.Handle(new ListSandboxUsageQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value
            .Where(u => u.SandboxId == oldest.Id || u.SandboxId == middle.Id || u.SandboxId == newest.Id)
            .Select(u => u.SandboxId)
            .Should().Equal(newest.Id, middle.Id, oldest.Id);
    }
}
