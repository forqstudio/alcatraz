using System.ComponentModel;
using Alcatraz.Cli.Common.Cli;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Sandboxes;

public class SandboxIdSettings : GlobalSettings
{
    [CommandArgument(0, "<id>")]
    [Description("Sandbox ID (UUID)")]
    public Guid SandboxId { get; init; }
}
