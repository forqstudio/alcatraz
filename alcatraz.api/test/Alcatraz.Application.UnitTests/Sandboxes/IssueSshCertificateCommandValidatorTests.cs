using FluentAssertions;
using Alcatraz.Application.Sandboxes.IssueSshCertificate;

namespace Alcatraz.Application.UnitTests.Sandboxes;

public class IssueSshCertificateCommandValidatorTests
{
    private readonly IssueSshCertificateCommandValidator _validator = new();

    [Theory]
    [InlineData("ssh-ed25519 AAAAC3 user@host")]
    [InlineData("ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTY user@host")]
    [InlineData("ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAAB user@host")]
    public void Validate_AcceptsKnownKeyTypes(string pubkey)
    {
        var result = _validator.Validate(new IssueSshCertificateCommand(Guid.NewGuid(), pubkey));
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("dsa-sha1 AAAA user@host")]
    [InlineData("ssh-ed25519")]
    public void Validate_RejectsInvalidPubkey(string pubkey)
    {
        var result = _validator.Validate(new IssueSshCertificateCommand(Guid.NewGuid(), pubkey));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RequiresSandboxId()
    {
        var result = _validator.Validate(new IssueSshCertificateCommand(Guid.Empty, "ssh-ed25519 AAAA user@host"));
        result.IsValid.Should().BeFalse();
    }
}
