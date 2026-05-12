using System.ComponentModel;
using Alcatraz.Cli.Common.Cli;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Sandboxes.Usage;

public sealed class UsageSettings : GlobalSettings
{
    [CommandArgument(0, "[id]")]
    [Description("Sandbox ID (UUID). Omit to list usage across all your sandboxes.")]
    public Guid? SandboxId { get; init; }
}
