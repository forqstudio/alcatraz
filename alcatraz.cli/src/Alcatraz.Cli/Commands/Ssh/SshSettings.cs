using System.ComponentModel;
using Alcatraz.Cli.Commands.Sandboxes;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Ssh;

public sealed class SshSettings : SandboxIdSettings
{
    [CommandOption("--no-proxy")]
    [Description("Skip the openssl s_client ProxyCommand (use for local plain-TCP sshd)")]
    public bool NoProxy { get; init; }

    [CommandArgument(1, "[remote-command]")]
    [Description("Optional command to run on the sandbox instead of an interactive shell")]
    public string? RemoteCommand { get; init; }
}
