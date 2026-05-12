using FluentAssertions;
using Alcatraz.Application.Sandboxes.RecordSandboxUsageSample;

namespace Alcatraz.Application.UnitTests.Sandboxes;

public class RecordSandboxUsageSampleCommandValidatorTests
{
    private readonly RecordSandboxUsageSampleCommandValidator validator = new();

    [Fact]
    public void Validate_WhenAllValid_Succeeds()
    {
        var result = validator.Validate(
            new RecordSandboxUsageSampleCommand(Guid.NewGuid(), DateTime.UtcNow, 1, 2, 3));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenSandboxIdEmpty_Fails()
    {
        var result = validator.Validate(
            new RecordSandboxUsageSampleCommand(Guid.Empty, DateTime.UtcNow, 1, 2, 3));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WhenSampledAtDefault_Fails()
    {
        var result = validator.Validate(
            new RecordSandboxUsageSampleCommand(Guid.NewGuid(), default, 1, 2, 3));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithNullCounters_Succeeds()
    {
        var result = validator.Validate(
            new RecordSandboxUsageSampleCommand(Guid.NewGuid(), DateTime.UtcNow, null, null, null));

        result.IsValid.Should().BeTrue();
    }
}
