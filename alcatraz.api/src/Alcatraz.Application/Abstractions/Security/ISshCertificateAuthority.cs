using Alcatraz.Domain.Abstractions;

namespace Alcatraz.Application.Abstractions.Security;

public interface ISshCertificateAuthority
{
    Task<Result<IssuedSshCertificate>> IssueAsync(
        string sshPublicKeyOpenSsh,
        string principal,
        TimeSpan ttl,
        string keyId,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}

public sealed record IssuedSshCertificate(
    string CertOpenSsh,
    DateTime ValidAfterUtc,
    DateTime ValidUntilUtc);

public static class SshCertificateErrors
{
    public static readonly Error SigningFailed = new(
        "Ssh.Certificate.SigningFailed",
        "Failed to sign the SSH certificate");

    public static readonly Error InvalidPublicKey = new(
        "Ssh.Certificate.InvalidPublicKey",
        "The provided SSH public key could not be parsed");
}
