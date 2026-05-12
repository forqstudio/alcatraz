using FluentAssertions;
using Alcatraz.Application.Sandboxes.RecordSandboxUsageSample;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes.Usage;
using NSubstitute;

namespace Alcatraz.Application.UnitTests.Sandboxes;

public class RecordSandboxUsageSampleCommandHandlerTests
{
    private static readonly DateTime SampledAt = new(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SandboxId = Guid.NewGuid();

    private readonly ISandboxUsageSampleRepository sampleRepository =
        Substitute.For<ISandboxUsageSampleRepository>();
    private readonly IUnitOfWork unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly RecordSandboxUsageSampleCommandHandler handler;

    public RecordSandboxUsageSampleCommandHandlerTests()
    {
        handler = new RecordSandboxUsageSampleCommandHandler(sampleRepository, unitOfWork);
    }

    private static RecordSandboxUsageSampleCommand Command() =>
        new(SandboxId, SampledAt, 1_000_000, 100, 200);

    [Fact]
    public async Task Handle_WhenSampleAlreadyExists_ReturnsSuccess_WithoutInserting()
    {
        sampleRepository.ExistsAsync(SandboxId, SampledAt, Arg.Any<CancellationToken>()).Returns(true);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        sampleRepository.DidNotReceive().Add(Arg.Any<SandboxUsageSample>());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HappyPath_AddsSample_AndSaves()
    {
        sampleRepository.ExistsAsync(SandboxId, SampledAt, Arg.Any<CancellationToken>()).Returns(false);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        sampleRepository.Received(1).Add(Arg.Is<SandboxUsageSample>(s =>
            s.SandboxId == SandboxId &&
            s.SampledAtUtc == SampledAt &&
            s.CpuUsageUsecCumulative == 1_000_000 &&
            s.NetRxBytesCumulative == 100 &&
            s.NetTxBytesCumulative == 200));
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_HappyPath_WithNullCounters_StillSaves()
    {
        // Worker may publish a sample with all-null sources if cgroup + fc metrics both failed.
        sampleRepository.ExistsAsync(SandboxId, SampledAt, Arg.Any<CancellationToken>()).Returns(false);

        var result = await handler.Handle(
            new RecordSandboxUsageSampleCommand(SandboxId, SampledAt, null, null, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        sampleRepository.Received(1).Add(Arg.Is<SandboxUsageSample>(s =>
            s.CpuUsageUsecCumulative == null &&
            s.NetRxBytesCumulative == null &&
            s.NetTxBytesCumulative == null));
    }
}
