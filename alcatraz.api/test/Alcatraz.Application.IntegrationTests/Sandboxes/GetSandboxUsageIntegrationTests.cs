using FluentAssertions;
using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Clock;
using Alcatraz.Application.Abstractions.Data;
using Alcatraz.Application.IntegrationTests.Infrastructure;
using Alcatraz.Application.Sandboxes.Usage;
using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Sandboxes.Usage;
using Alcatraz.Domain.Users;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Alcatraz.Application.IntegrationTests.Sandboxes;

public class GetSandboxUsageIntegrationTests : BaseIntegrationTest
{
    private static readonly DateTime BootedAt = new(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);

    private readonly IntegrationTestWebAppFactory _factory;

    public GetSandboxUsageIntegrationTests(IntegrationTestWebAppFactory factory)
        : base(factory)
    {
        _factory = factory;
    }

    private static SandboxRuntimeInfo Runtime() =>
        new(
            Host: "172.16.0.10",
            Port: 22,
            ActualVcpus: 4,
            ActualMemoryMib: 8192,
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
            new Email($"sandbox-usage-{label}+{Guid.NewGuid():N}@test.com"));

    private static SandboxUsageRecord FinaliseFor(Sandbox sandbox, DateTime windowEnd)
    {
        sandbox.MarkDeleting(windowEnd);
        var final = new SandboxUsageFinal(
            VmBootedAtUtc: BootedAt,
            FinalisedAtUtc: windowEnd,
            TotalCpuUsageUsec: 5_000_000,
            TotalNetRxBytes: 1_500_000,
            TotalNetTxBytes: 1_800_000,
            SampleCount: 5);
        return SandboxUsageRecord.Finalise(sandbox, final, windowEnd).Value;
    }

    private static IDateTimeProvider FixedClock(DateTime utcNow)
    {
        var clock = Substitute.For<IDateTimeProvider>();
        clock.UtcNow.Returns(utcNow);
        return clock;
    }

    [Fact]
    public async Task Handle_HappyPath_ReturnsFinalisedRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Alcatraz.Infrastructure.ApplicationDbContext>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var sqlFactory = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        var user = MakeUser("happy");
        user.SetIdentityId(Guid.NewGuid().ToString());
        userRepo.Add(user);
        await dbContext.SaveChangesAsync();

        var sandbox = Sandbox.Request(user.Id, 4, 8192, BootedAt);
        sandbox.MarkRunning(Runtime(), BootedAt);
        dbContext.Set<Sandbox>().Add(sandbox);
        await dbContext.SaveChangesAsync();

        dbContext.Set<SandboxUsageRecord>().Add(FinaliseFor(sandbox, BootedAt.AddMinutes(5)));
        await dbContext.SaveChangesAsync();

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(user.Id);
        var handler = new GetSandboxUsageQueryHandler(sqlFactory, userContext, FixedClock(BootedAt.AddMinutes(6)));

        var result = await handler.Handle(new GetSandboxUsageQuery(sandbox.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.SandboxId.Should().Be(sandbox.Id);
        result.Value.OwnerUserId.Should().Be(user.Id);
        result.Value.Finalised.Should().BeTrue();
        result.Value.ProvisionedVcpuSeconds.Should().Be(1200);
        result.Value.ProvisionedMemoryMibSeconds.Should().Be(2_457_600);
        result.Value.ActualCpuUsageUsec.Should().Be(5_000_000);
        result.Value.SampleCount.Should().Be(5);
        result.Value.FinalisedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_WhenFinalisedAndOwnedByAnotherUser_ReturnsSandboxNotFound()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Alcatraz.Infrastructure.ApplicationDbContext>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var sqlFactory = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        var owner = MakeUser("owner");
        var intruder = MakeUser("intruder");
        owner.SetIdentityId(Guid.NewGuid().ToString());
        intruder.SetIdentityId(Guid.NewGuid().ToString());
        userRepo.Add(owner);
        userRepo.Add(intruder);
        await dbContext.SaveChangesAsync();

        var sandbox = Sandbox.Request(owner.Id, 4, 8192, BootedAt);
        sandbox.MarkRunning(Runtime(), BootedAt);
        dbContext.Set<Sandbox>().Add(sandbox);
        await dbContext.SaveChangesAsync();

        dbContext.Set<SandboxUsageRecord>().Add(FinaliseFor(sandbox, BootedAt.AddMinutes(5)));
        await dbContext.SaveChangesAsync();

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(intruder.Id);
        var handler = new GetSandboxUsageQueryHandler(sqlFactory, userContext, FixedClock(BootedAt.AddMinutes(6)));

        var result = await handler.Handle(new GetSandboxUsageQuery(sandbox.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxUsageErrors.SandboxNotFound);
    }

    [Fact]
    public async Task Handle_WhenRunning_ReturnsInflightView_FromCurrentClock()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Alcatraz.Infrastructure.ApplicationDbContext>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var sqlFactory = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        var user = MakeUser("inflight");
        user.SetIdentityId(Guid.NewGuid().ToString());
        userRepo.Add(user);
        await dbContext.SaveChangesAsync();

        var sandbox = Sandbox.Request(user.Id, 4, 8192, BootedAt);
        sandbox.MarkRunning(Runtime(), BootedAt);
        dbContext.Set<Sandbox>().Add(sandbox);
        await dbContext.SaveChangesAsync();

        // Two samples; latest cumulative counters should appear in the response.
        dbContext.Set<SandboxUsageSample>().AddRange(
            SandboxUsageSample.Record(sandbox.Id, BootedAt.AddSeconds(60),  1_000_000, 1_000, 2_000),
            SandboxUsageSample.Record(sandbox.Id, BootedAt.AddSeconds(120), 3_000_000, 5_000, 6_000));
        await dbContext.SaveChangesAsync();

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(user.Id);
        // Window of 3 minutes from ReadyAtUtc → BootedAt + 180s.
        var handler = new GetSandboxUsageQueryHandler(sqlFactory, userContext, FixedClock(BootedAt.AddSeconds(180)));

        var result = await handler.Handle(new GetSandboxUsageQuery(sandbox.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Finalised.Should().BeFalse();
        result.Value.FinalisedAtUtc.Should().BeNull();
        result.Value.BillingWindowStartUtc.Should().Be(BootedAt);
        result.Value.BillingWindowEndUtc.Should().Be(BootedAt.AddSeconds(180));
        // 180s × 4 vcpus = 720; × 8192 MiB = 1,474,560
        result.Value.ProvisionedVcpuSeconds.Should().Be(720);
        result.Value.ProvisionedMemoryMibSeconds.Should().Be(1_474_560);
        result.Value.ActualCpuUsageUsec.Should().Be(3_000_000);
        result.Value.ActualNetRxBytes.Should().Be(5_000);
        result.Value.ActualNetTxBytes.Should().Be(6_000);
        result.Value.SampleCount.Should().Be(2);
    }

    [Fact]
    public async Task Handle_WhenStillProvisioning_ReturnsZeroWindow()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Alcatraz.Infrastructure.ApplicationDbContext>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var sqlFactory = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        var user = MakeUser("provisioning");
        user.SetIdentityId(Guid.NewGuid().ToString());
        userRepo.Add(user);
        await dbContext.SaveChangesAsync();

        // No MarkRunning — still in Provisioning, ReadyAtUtc + actuals are null.
        var sandbox = Sandbox.Request(user.Id, 4, 8192, BootedAt);
        dbContext.Set<Sandbox>().Add(sandbox);
        await dbContext.SaveChangesAsync();

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(user.Id);
        var handler = new GetSandboxUsageQueryHandler(sqlFactory, userContext, FixedClock(BootedAt.AddMinutes(1)));

        var result = await handler.Handle(new GetSandboxUsageQuery(sandbox.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Finalised.Should().BeFalse();
        result.Value.ProvisionedVcpuSeconds.Should().Be(0);
        result.Value.ProvisionedMemoryMibSeconds.Should().Be(0);
        result.Value.BillingWindowStartUtc.Should().Be(result.Value.BillingWindowEndUtc);
    }

    [Fact]
    public async Task Handle_WhenSandboxAbsent_ReturnsSandboxNotFound()
    {
        using var scope = _factory.Services.CreateScope();
        var sqlFactory = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(Guid.NewGuid());
        var handler = new GetSandboxUsageQueryHandler(sqlFactory, userContext, FixedClock(BootedAt));

        var result = await handler.Handle(new GetSandboxUsageQuery(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxUsageErrors.SandboxNotFound);
    }
}
