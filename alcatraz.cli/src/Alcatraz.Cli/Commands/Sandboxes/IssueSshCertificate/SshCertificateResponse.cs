namespace Alcatraz.Cli.Commands.Sandboxes.IssueSshCertificate;

public sealed record SshCertificateResponse(
    string Cert,
    DateTime ValidUntilUtc,
    string GatewayHost,
    int GatewayPort);
