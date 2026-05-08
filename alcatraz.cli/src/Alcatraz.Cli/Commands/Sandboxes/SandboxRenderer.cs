using System.Text.Json;
using Spectre.Console;

namespace Alcatraz.Cli.Commands.Sandboxes;

internal static class SandboxRenderer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static void Render(SandboxResponse sandbox, bool asJson)
    {
        if (asJson)
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(sandbox, Json));
            return;
        }

        var table = new Table().AddColumn("Field").AddColumn("Value");
        table.AddRow("Id", sandbox.Id.ToString());
        table.AddRow("Status", FormatStatus(sandbox.Status));
        table.AddRow("vCPUs", sandbox.Vcpus.ToString(System.Globalization.CultureInfo.InvariantCulture));
        table.AddRow("Memory (MiB)", sandbox.MemoryMib.ToString(System.Globalization.CultureInfo.InvariantCulture));
        table.AddRow("Created (UTC)", sandbox.CreatedOnUtc.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
        if (sandbox.DeletedOnUtc is { } d)
        {
            table.AddRow("Deleted (UTC)", d.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
        }
        AnsiConsole.Write(table);
    }

    public static void RenderList(IReadOnlyList<SandboxResponse> sandboxes, bool asJson)
    {
        if (asJson)
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(sandboxes, Json));
            return;
        }

        if (sandboxes.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No sandboxes.[/]");
            return;
        }

        var table = new Table()
            .AddColumn("Id")
            .AddColumn("Status")
            .AddColumn(new TableColumn("vCPUs").RightAligned())
            .AddColumn(new TableColumn("Memory (MiB)").RightAligned())
            .AddColumn("Created (UTC)");

        foreach (var s in sandboxes)
        {
            table.AddRow(
                s.Id.ToString(),
                FormatStatus(s.Status),
                s.Vcpus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                s.MemoryMib.ToString(System.Globalization.CultureInfo.InvariantCulture),
                s.CreatedOnUtc.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
    }

    private static string FormatStatus(int status) =>
        status switch
        {
            (int)SandboxStatus.Provisioning => "Provisioning",
            (int)SandboxStatus.Running => "Running",
            (int)SandboxStatus.Deleting => "Deleting",
            (int)SandboxStatus.Deleted => "Deleted",
            (int)SandboxStatus.Failed => "Failed",
            _ => $"Unknown({status})",
        };
}
