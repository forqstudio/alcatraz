using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Alcatraz.Cli.Commands.Sandboxes.Usage;

internal static class UsageRenderer
{
    private const string Dash = "—";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static void Render(SandboxUsageResponse usage, bool asJson)
    {
        if (asJson)
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(usage, Json));
            return;
        }

        AnsiConsole.Write(BuildPanel(usage));
    }

    public static void RenderList(IReadOnlyList<SandboxUsageResponse> usages, bool asJson)
    {
        if (asJson)
        {
            AnsiConsole.WriteLine(JsonSerializer.Serialize(usages, Json));
            return;
        }

        if (usages.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No usage records yet. Records are written after a sandbox exits.[/]");
            return;
        }

        foreach (var usage in usages)
        {
            AnsiConsole.Write(BuildPanel(usage));
        }
    }

    private static Panel BuildPanel(SandboxUsageResponse u)
    {
        var window = u.BillingWindowEndUtc - u.BillingWindowStartUtc;

        var compact = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn(new GridColumn().PadRight(4))
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn();

        AddPair(compact,
            "Window start", FormatDate(u.BillingWindowStartUtc),
            "Window end", u.Finalised ? FormatDate(u.BillingWindowEndUtc) : "now");
        AddPair(compact,
            "Duration", FormatDuration(window),
            "Samples", u.SampleCount.ToString(CultureInfo.InvariantCulture));

        var provisioned = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn();

        AddRow(provisioned, "vCPU-seconds", FormatLong(u.ProvisionedVcpuSeconds));
        AddRow(provisioned, "MiB-seconds", FormatLong(u.ProvisionedMemoryMibSeconds));

        var actual = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn();

        AddRow(actual, "CPU time", FormatCpuTime(u.ActualCpuUsageUsec));
        AddRow(actual, "Network rx", FormatBytes(u.ActualNetRxBytes));
        AddRow(actual, "Network tx", FormatBytes(u.ActualNetTxBytes));

        var content = new Rows(
            compact,
            new Text(string.Empty),
            new Markup("[bold]Provisioned[/]"),
            provisioned,
            new Text(string.Empty),
            new Markup("[bold]Actual[/]"),
            actual);

        var status = u.Finalised && u.FinalisedAtUtc.HasValue
            ? $"[dim]finalised {FormatDate(u.FinalisedAtUtc.Value)}[/]"
            : "[yellow]in progress[/]";

        return new Panel(content)
        {
            Header = new PanelHeader($"[bold]{u.SandboxId}[/]  {status}"),
            Border = BoxBorder.Rounded,
            Expand = true,
        };
    }

    private static void AddPair(
        Grid grid,
        string label1, string value1,
        string label2, string value2)
    {
        grid.AddRow(
            new Text(label1),
            ValueRenderable(value1),
            new Text(label2),
            ValueRenderable(value2));
    }

    private static void AddRow(Grid grid, string label, string value)
    {
        grid.AddRow(new Text(label), ValueRenderable(value));
    }

    private static IRenderable ValueRenderable(string value) =>
        value == Dash ? new Markup($"[dim]{Dash}[/]") : new Text(value);

    private static string FormatDate(DateTime value) =>
        value.ToString("u", CultureInfo.InvariantCulture);

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalSeconds < 1) return "0s";
        if (span.TotalMinutes < 1) return $"{span.TotalSeconds:F0}s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalDays}d {span.Hours}h";
    }

    private static string FormatLong(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatCpuTime(long? usec)
    {
        if (!usec.HasValue) return Dash;
        var seconds = usec.Value / 1_000_000d;
        if (seconds < 1) return $"{usec.Value / 1000d:F1} ms";
        if (seconds < 60) return $"{seconds:F2} s";
        if (seconds < 3600) return $"{seconds / 60:F2} min";
        return $"{seconds / 3600:F2} h";
    }

    private static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue) return Dash;
        var v = (double)bytes.Value;
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0
            ? $"{bytes.Value:N0} {units[i]}"
            : $"{v:F2} {units[i]}";
    }
}
