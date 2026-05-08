using System.ComponentModel;
using Alcatraz.Cli.Common.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.Sandboxes.CreateSandbox;

public sealed class CreateSandboxSettings : GlobalSettings
{
    [CommandOption("--vcpus <N>")]
    [Description("Number of virtual CPUs (1-16)")]
    public int Vcpus { get; init; } = 2;

    [CommandOption("--memory <MIB>")]
    [Description("Memory in MiB (512-32768, multiple of 256)")]
    public int MemoryMib { get; init; } = 2048;

    public override ValidationResult Validate()
    {
        if (Vcpus is < 1 or > 16)
        {
            return ValidationResult.Error("--vcpus must be between 1 and 16");
        }

        if (MemoryMib is < 512 or > 32768)
        {
            return ValidationResult.Error("--memory must be between 512 and 32768 MiB");
        }

        if (MemoryMib % 256 != 0)
        {
            return ValidationResult.Error("--memory must be a multiple of 256 MiB");
        }

        return ValidationResult.Success();
    }
}
