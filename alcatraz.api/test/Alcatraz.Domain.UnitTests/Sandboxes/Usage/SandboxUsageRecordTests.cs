using FluentAssertions;
using Alcatraz.Domain.Sandboxes;
using Alcatraz.Domain.Sandboxes.Events;
using Alcatraz.Domain.Sandboxes.Usage;
using Alcatraz.Domain.UnitTests.Infrastructure;

namespace Alcatraz.Domain.UnitTests.Sandboxes.Usage;

public class SandboxUsageRecordTests : BaseTest
{
    private static readonly DateTime BootedAt = new(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OwnerUserId = Guid.NewGuid();

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

    private static Sandbox RunningSandbox(int vcpus = 4, int memoryMib = 8192)
    {
        var sandbox = Sandbox.Request(OwnerUserId, vcpus, memoryMib, BootedAt);
        sandbox.MarkRunning(Runtime(vcpus, memoryMib), BootedAt);
        return sandbox;
    }

    [Fact]
    public void Finalise_HappyPath_ComputesProvisionedTotals_AndRaisesEvent()
    {
        var sandbox = RunningSandbox(vcpus: 4, memoryMib: 8192);
        sandbox.MarkDeleting(BootedAt.AddMinutes(5));
        sandbox.MarkDestroyed(BootedAt.AddMinutes(5).AddSeconds(36));

        var final = new SandboxUsageFinal(
            VmBootedAtUtc: BootedAt,
            FinalisedAtUtc: BootedAt.AddMinutes(5).AddSeconds(36),
            TotalCpuUsageUsec: 5_000_000,
            TotalNetRxBytes: 1_500_000,
            TotalNetTxBytes: 1_800_000,
            SampleCount: 5);

        var utcNow = BootedAt.AddMinutes(5).AddSeconds(37);
        var result = SandboxUsageRecord.Finalise(sandbox, final, utcNow);

        result.IsSuccess.Should().BeTrue();
        var record = result.Value;

        record.SandboxId.Should().Be(sandbox.Id);
        record.BillingWindowStartUtc.Should().Be(BootedAt);
        record.BillingWindowEndUtc.Should().Be(BootedAt.AddMinutes(5));
        // 5 minutes = 300 seconds × 4 vcpus = 1200; × 8192 MiB = 2,457,600
        record.ProvisionedVcpuSeconds.Should().Be(1200);
        record.ProvisionedMemoryMibSeconds.Should().Be(2_457_600);
        record.ActualCpuUsageUsec.Should().Be(5_000_000);
        record.ActualNetRxBytes.Should().Be(1_500_000);
        record.ActualNetTxBytes.Should().Be(1_800_000);
        record.SampleCount.Should().Be(5);
        record.FinalisedAtUtc.Should().Be(utcNow);

        AssertDomainEventWasPublished<SandboxUsageRecordedDomainEvent>(record)
            .SandboxId.Should().Be(sandbox.Id);
    }

    [Fact]
    public void Finalise_WhenDeletedOnUtcIsNull_UsesFinalisedAtUtcAsWindowEnd()
    {
        // Worker crashed mid-sandbox: vm.usage_final arrives but vm.destroyed never did,
        // so Sandbox.DeletedOnUtc is still null. Window should clamp to FinalisedAtUtc.
        var sandbox = RunningSandbox();
        var finalisedAt = BootedAt.AddMinutes(10);

        var final = new SandboxUsageFinal(
            VmBootedAtUtc: BootedAt,
            FinalisedAtUtc: finalisedAt,
            TotalCpuUsageUsec: null,
            TotalNetRxBytes: null,
            TotalNetTxBytes: null,
            SampleCount: 9);

        var result = SandboxUsageRecord.Finalise(sandbox, final, finalisedAt.AddSeconds(1));

        result.IsSuccess.Should().BeTrue();
        result.Value.BillingWindowEndUtc.Should().Be(finalisedAt);
        result.Value.ActualCpuUsageUsec.Should().BeNull();
    }

    [Fact]
    public void Finalise_WhenSandboxNeverReachedRunning_ReturnsNotFinalisable()
    {
        // Sandbox stayed in Provisioning then failed before vm.ready — ReadyAtUtc is null.
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, BootedAt);

        var final = new SandboxUsageFinal(
            VmBootedAtUtc: BootedAt,
            FinalisedAtUtc: BootedAt.AddMinutes(1),
            TotalCpuUsageUsec: null,
            TotalNetRxBytes: null,
            TotalNetTxBytes: null,
            SampleCount: 0);

        var result = SandboxUsageRecord.Finalise(sandbox, final, BootedAt.AddMinutes(1));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SandboxUsageErrors.SandboxNotFinalisable);
    }

    [Fact]
    public void Finalise_WhenWindowEndPrecedesStart_ClampsWindowToZeroSeconds()
    {
        // Defensive: deletion timestamp earlier than ready — provisioned totals shouldn't go negative.
        var sandbox = RunningSandbox();
        sandbox.MarkDeleting(BootedAt.AddMinutes(-1));

        var final = new SandboxUsageFinal(
            VmBootedAtUtc: BootedAt,
            FinalisedAtUtc: BootedAt.AddSeconds(1),
            TotalCpuUsageUsec: null,
            TotalNetRxBytes: null,
            TotalNetTxBytes: null,
            SampleCount: 0);

        var result = SandboxUsageRecord.Finalise(sandbox, final, BootedAt);

        result.IsSuccess.Should().BeTrue();
        result.Value.ProvisionedVcpuSeconds.Should().Be(0);
        result.Value.ProvisionedMemoryMibSeconds.Should().Be(0);
    }
}
