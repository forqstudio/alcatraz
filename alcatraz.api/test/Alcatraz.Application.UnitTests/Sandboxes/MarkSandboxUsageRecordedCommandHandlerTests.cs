using FluentAssertions;
using Alcatraz.Application.Abstractions.Clock;
using Alcatraz.Application.Sandboxes.MarkSandboxUsageRecorded;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Sandboxes.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Alcatraz.Application.UnitTests.Sandboxes;

public class MarkSandboxUsageRecordedCommandHandlerTests
{
    private static readonly DateTime BootedAt = new(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OwnerUserId = Guid.NewGuid();

    private readonly ISandboxRepository sandboxRepository = Substitute.For<ISandboxRepository>();
    private readonly ISandboxUsageRecordRepository usageRecordRepository =
        Substitute.For<ISandboxUsageRecordRepository>();
    private readonly IUnitOfWork unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MarkSandboxUsageRecordedCommandHandler handler;

    public MarkSandboxUsageRecordedCommandHandlerTests()
    {
        var clock = Substitute.For<IDateTimeProvider>();
        clock.UtcNow.Returns(BootedAt.AddMinutes(10));
        handler = new MarkSandboxUsageRecordedCommandHandler(
            sandboxRepository,
            usageRecordRepository,
            unitOfWork,
            clock,
            NullLogger<MarkSandboxUsageRecordedCommandHandler>.Instance);
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

    private static Sandbox RunningSandbox()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 4, 8192, BootedAt);
        sandbox.MarkRunning(Runtime(), BootedAt);
        return sandbox;
    }

    private static SandboxUsageFinal Final() =>
        new(
            VmBootedAtUtc: BootedAt,
            FinalisedAtUtc: BootedAt.AddMinutes(5),
            TotalCpuUsageUsec: 1_000_000,
            TotalNetRxBytes: 2_000,
            TotalNetTxBytes: 3_000,
            SampleCount: 5);

    [Fact]
    public async Task Handle_WhenRecordAlreadyExists_ReturnsSuccess_WithoutSaving()
    {
        var sandboxId = Guid.NewGuid();
        // Build a stub record using reflection via the existing factory path on a real sandbox.
        var sandbox = RunningSandbox();
        sandbox.MarkDeleting(BootedAt.AddMinutes(5));
        var existing = SandboxUsageRecord.Finalise(sandbox, Final(), BootedAt.AddMinutes(5)).Value;
        usageRecordRepository.GetBySandboxIdAsync(sandboxId, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await handler.Handle(
            new MarkSandboxUsageRecordedCommand(sandboxId, Final()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        usageRecordRepository.DidNotReceive().Add(Arg.Any<SandboxUsageRecord>());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSandboxNotFound_ReturnsSandboxNotFound()
    {
        var sandboxId = Guid.NewGuid();
        usageRecordRepository.GetBySandboxIdAsync(sandboxId, Arg.Any<CancellationToken>())
            .Returns((SandboxUsageRecord?)null);
        sandboxRepository.GetByIdAsync(sandboxId, Arg.Any<CancellationToken>())
            .Returns((Sandbox?)null);

        var result = await handler.Handle(
            new MarkSandboxUsageRecordedCommand(sandboxId, Final()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxUsageErrors.SandboxNotFound);
        usageRecordRepository.DidNotReceive().Add(Arg.Any<SandboxUsageRecord>());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSandboxNeverRan_PropagatesNotFinalisable()
    {
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, BootedAt);
        usageRecordRepository.GetBySandboxIdAsync(sandbox.Id, Arg.Any<CancellationToken>())
            .Returns((SandboxUsageRecord?)null);
        sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await handler.Handle(
            new MarkSandboxUsageRecordedCommand(sandbox.Id, Final()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxUsageErrors.SandboxNotFinalisable);
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HappyPath_AddsRecord_AndSaves()
    {
        var sandbox = RunningSandbox();
        sandbox.MarkDeleting(BootedAt.AddMinutes(5));
        usageRecordRepository.GetBySandboxIdAsync(sandbox.Id, Arg.Any<CancellationToken>())
            .Returns((SandboxUsageRecord?)null);
        sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await handler.Handle(
            new MarkSandboxUsageRecordedCommand(sandbox.Id, Final()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        usageRecordRepository.Received(1).Add(Arg.Is<SandboxUsageRecord>(r =>
            r.SandboxId == sandbox.Id &&
            r.ProvisionedVcpuSeconds == 1200 &&
            r.ProvisionedMemoryMibSeconds == 2_457_600 &&
            r.ActualCpuUsageUsec == 1_000_000 &&
            r.SampleCount == 5));
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
