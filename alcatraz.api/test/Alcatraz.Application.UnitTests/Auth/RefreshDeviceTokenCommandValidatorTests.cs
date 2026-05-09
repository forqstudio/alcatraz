using FluentAssertions;
using Alcatraz.Application.Auth.RefreshDeviceToken;

namespace Alcatraz.Application.UnitTests.Auth;

public class RefreshDeviceTokenCommandValidatorTests
{
    private readonly RefreshDeviceTokenCommandValidator _validator = new();

    [Fact]
    public void Validate_NonEmptyToken_Passes()
    {
        var result = _validator.Validate(new RefreshDeviceTokenCommand("rt"));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyToken_Fails()
    {
        var result = _validator.Validate(new RefreshDeviceTokenCommand(string.Empty));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_OverlongToken_Fails()
    {
        var result = _validator.Validate(new RefreshDeviceTokenCommand(new string('x', 8193)));

        result.IsValid.Should().BeFalse();
    }
}
