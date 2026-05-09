using FluentValidation;

namespace Alcatraz.Application.Sandboxes.IssueSshCertificate;

internal sealed class IssueSshCertificateCommandValidator : AbstractValidator<IssueSshCertificateCommand>
{
    private static readonly string[] AllowedPrefixes =
    {
        "ssh-ed25519 ",
        "ecdsa-sha2-nistp256 ",
        "ecdsa-sha2-nistp384 ",
        "ecdsa-sha2-nistp521 ",
        "ssh-rsa ",
    };

    public IssueSshCertificateCommandValidator()
    {
        RuleFor(c => c.SandboxId).NotEmpty();

        RuleFor(c => c.SshPublicKey)
            .NotEmpty()
            .MaximumLength(4096)
            .Must(BeAcceptableKeyType)
            .WithMessage("SshPublicKey must start with one of: " +
                         string.Join(", ", AllowedPrefixes).TrimEnd(',', ' '));
    }

    private static bool BeAcceptableKeyType(string sshPublicKey)
    {
        if (string.IsNullOrWhiteSpace(sshPublicKey))
        {
            return false;
        }

        var trimmed = sshPublicKey.TrimStart();

        foreach (var prefix in AllowedPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
