using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Alcatraz.Cli.Commands.Sandboxes;

internal static class SandboxRenderer
{
    private const string Dash = "—";

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

        AnsiConsole.Write(BuildPanel(sandbox));
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

        foreach (var sandbox in sandboxes)
        {
            AnsiConsole.Write(BuildPanel(sandbox));
        }
    }

    private static Panel BuildPanel(SandboxResponse s)
    {
        var compact = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn(new GridColumn().PadRight(4))
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn();

        AddPair(compact,
            "vCPUs", $"{IntOrDash(s.ActualVcpus)} / {s.Vcpus}",
            "Memory", $"{IntOrDash(s.ActualMemoryMib)} / {s.MemoryMib} MiB");
        AddPair(compact,
            "Created", FormatDate(s.CreatedOnUtc),
            "Ready", FormatDate(s.ReadyAtUtc));
        AddPair(compact,
            "Endpoint", FormatEndpoint(s.Host, s.Port),
            "Boot", s.BootDurationMs.HasValue
                ? $"{s.BootDurationMs.Value.ToString(CultureInfo.InvariantCulture)} ms"
                : Dash);
        AddPair(compact,
            "NFS", s.NfsPort.HasValue ? $"port {s.NfsPort.Value}" : Dash,
            "Slot", IntOrDash(s.WorkerSlotIndex));

        var details = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn();

        AddRow(details, "VMM", FormatVmm(s));
        AddRow(details, "Network", FormatNetwork(s));
        AddRow(details, "Socket", StrOrDash(s.SocketPath));
        AddRow(details, "Rootfs", StrOrDash(s.RootfsPath));
        AddRow(details, "Kernel", StrOrDash(s.KernelPath));
        if (s.DeletedOnUtc.HasValue)
        {
            AddRow(details, "Deleted", FormatDate(s.DeletedOnUtc.Value));
        }

        var content = new Rows(compact, new Text(string.Empty), details);

        return new Panel(content)
        {
            Header = new PanelHeader($"[bold]{s.Id}[/]  {FormatStatusMarkup(s.Status)}"),
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

    private static string FormatEndpoint(string? host, int? port)
    {
        var hasHost = !string.IsNullOrEmpty(host);
        if (!hasHost && !port.HasValue) return Dash;
        if (!hasHost) return $":{port}";
        if (!port.HasValue) return host!;
        return $"{host}:{port}";
    }

    private static string FormatVmm(SandboxResponse s)
    {
        var hasVersion = !string.IsNullOrEmpty(s.VmmVersion);
        var hasState = !string.IsNullOrEmpty(s.VmmState);
        var hasPid = s.FirecrackerPid.HasValue;

        if (!hasVersion && !hasState && !hasPid) return Dash;

        var parts = new List<string>();
        if (hasVersion) parts.Add($"Firecracker {s.VmmVersion}");

        var detail = new List<string>();
        if (hasState) detail.Add(s.VmmState!);
        if (hasPid) detail.Add($"PID {s.FirecrackerPid!.Value.ToString(CultureInfo.InvariantCulture)}");
        if (detail.Count > 0) parts.Add($"({string.Join(", ", detail)})");

        return string.Join(" ", parts);
    }

    private static string FormatNetwork(SandboxResponse s)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(s.TapDevice)) parts.Add(s.TapDevice);
        if (!string.IsNullOrEmpty(s.MacAddress)) parts.Add(s.MacAddress);

        var hasVm = !string.IsNullOrEmpty(s.VmIp);
        var hasGw = !string.IsNullOrEmpty(s.HostGatewayIp);
        if (hasVm || hasGw)
        {
            var vm = hasVm ? s.VmIp! : Dash;
            var gw = hasGw ? s.HostGatewayIp! : Dash;
            parts.Add($"{vm} ↔ {gw}");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : Dash;
    }

    private static string FormatStatusMarkup(int status) =>
        status switch
        {
            (int)SandboxStatus.Provisioning => "[yellow]Provisioning[/]",
            (int)SandboxStatus.Running => "[green]Running[/]",
            (int)SandboxStatus.Deleting => "[yellow]Deleting[/]",
            (int)SandboxStatus.Deleted => "[dim]Deleted[/]",
            (int)SandboxStatus.Failed => "[red]Failed[/]",
            _ => $"[dim]Unknown({status})[/]",
        };

    private static string StrOrDash(string? value) =>
        string.IsNullOrEmpty(value) ? Dash : value;

    private static string IntOrDash(int? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : Dash;

    private static string FormatDate(DateTime value) =>
        value.ToString("u", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTime? value) =>
        value.HasValue ? value.Value.ToString("u", CultureInfo.InvariantCulture) : Dash;
}
