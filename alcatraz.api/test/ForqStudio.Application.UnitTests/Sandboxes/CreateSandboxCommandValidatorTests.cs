using FluentAssertions;
using ForqStudio.Application.Sandboxes.CreateSandbox;

namespace ForqStudio.Application.UnitTests.Sandboxes;

public class CreateSandboxCommandValidatorTests
{
    private readonly CreateSandboxCommandValidator _validator = new();

    [Theory]
    [InlineData(0, 2048)]
    [InlineData(17, 2048)]
    [InlineData(2, 256)]
    [InlineData(2, 33000)]
    [InlineData(2, 1000)]
    public void Validate_ShouldFail_WhenOutOfBounds(int vcpus, int memoryMib)
    {
        var result = _validator.Validate(new CreateSandboxCommand(vcpus, memoryMib));

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, 512)]
    [InlineData(4, 4096)]
    [InlineData(16, 32768)]
    public void Validate_ShouldSucceed_WhenWithinBounds(int vcpus, int memoryMib)
    {
        var result = _validator.Validate(new CreateSandboxCommand(vcpus, memoryMib));

        result.IsValid.Should().BeTrue();
    }
}
