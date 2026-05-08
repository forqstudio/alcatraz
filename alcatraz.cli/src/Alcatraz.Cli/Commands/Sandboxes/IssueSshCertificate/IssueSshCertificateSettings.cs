using System.ComponentModel;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Sandboxes.IssueSshCertificate;

public sealed class IssueSshCertificateSettings : SandboxIdSettings
{
    [CommandOption("--public-key <PATH>")]
    [Description("Workstation public key (default: ~/.config/alcatraz/id_alcatraz.pub, auto-generated if absent)")]
    public string? PublicKeyPath { get; init; }

    [CommandOption("--out <PATH>")]
    [Description("Where to write the cert (default: ~/.config/alcatraz/certs/<id>-cert.pub)")]
    public string? OutputPath { get; init; }
}
