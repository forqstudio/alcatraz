namespace ForqStudio.Infrastructure.Security;

public sealed class SshCertificateAuthorityOptions
{
    public string PrivateKeyPath { get; set; } = string.Empty;

    public int DefaultTtlHours { get; set; } = 24;

    public string SshKeygenPath { get; set; } = "ssh-keygen";
}
