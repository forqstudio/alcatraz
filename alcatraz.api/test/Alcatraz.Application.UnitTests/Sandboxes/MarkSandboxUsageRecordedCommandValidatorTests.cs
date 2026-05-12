using FluentAssertions;
using Alcatraz.Application.Sandboxes.MarkSandboxUsageRecorded;
using Alcatraz.Domain.Sandboxes.Usage;

namespace Alcatraz.Application.UnitTests.Sandboxes;

public class MarkSandboxUsageRecordedCommandValidatorTests
{
    private readonly MarkSandboxUsageRecordedCommandValidator validator = new();

    private static SandboxUsageFinal Final() =>
        new(
            VmBootedAtUtc: DateTime.UtcNow.AddMinutes(-5),
            FinalisedAtUtc: DateTime.UtcNow,
            TotalCpuUsageUsec: 100,
            TotalNetRxBytes: 200,
            TotalNetTxBytes: 300,
            SampleCount: 5);

    [Fact]
    public void Validate_WhenAllValid_Succeeds()
    {
        var result = validator.Validate(new MarkSandboxUsageRecordedCommand(Guid.NewGuid(), Final()));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenSandboxIdEmpty_Fails()
    {
        var result = validator.Validate(new MarkSandboxUsageRecordedCommand(Guid.Empty, Final()));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenVmBootedAtUtcDefault_Fails()
    {
        var final = Final() with { VmBootedAtUtc = default };
        var result = validator.Validate(new MarkSandboxUsageRecordedCommand(Guid.NewGuid(), final));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenFinalisedAtUtcDefault_Fails()
    {
        var final = Final() with { FinalisedAtUtc = default };
        var result = validator.Validate(new MarkSandboxUsageRecordedCommand(Guid.NewGuid(), final));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenSampleCountNegative_Fails()
    {
        var final = Final() with { SampleCount = -1 };
        var result = validator.Validate(new MarkSandboxUsageRecordedCommand(Guid.NewGuid(), final));

        result.IsValid.Should().BeFalse();
    }
}
