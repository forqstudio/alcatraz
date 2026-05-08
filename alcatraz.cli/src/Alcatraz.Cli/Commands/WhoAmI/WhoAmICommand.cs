using Alcatraz.Cli.Common.Authentication;
using Alcatraz.Cli.Common.Bootstrap;
using Alcatraz.Cli.Common.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcatraz.Cli.Commands.WhoAmI;

internal sealed class WhoAmICommand(
    ITokenStore tokens,
    CancellationContext cancellation) : AsyncCommand<GlobalSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings)
    {
        var ct = cancellation.Token;
        var stored = await tokens.LoadAsync(ct);
        if (stored is null)
        {
            AnsiConsole.MarkupLine("[yellow]Not logged in.[/] Run `alcatraz login` to sign in.");
            return ExitCodes.NotLoggedIn;
        }

        var claims = JwtPayloadDecoder.TryDecode(stored.IdToken ?? stored.AccessToken);
        var subject = claims?.Sub ?? "(unknown)";
        var email = claims?.Email ?? "(unknown)";
        var username = claims?.PreferredUsername ?? "(unknown)";
        var expiresIn = stored.AccessTokenExpiresAtUtc - DateTime.UtcNow;

        if (settings.Json)
        {
            var payload = new
            {
                sub = subject,
                email,
                preferredUsername = username,
                accessTokenExpiresAtUtc = stored.AccessTokenExpiresAtUtc,
            };
            AnsiConsole.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            var table = new Table().HideHeaders().AddColumn("k").AddColumn("v");
            table.AddRow("Subject", subject);
            table.AddRow("Email", email);
            table.AddRow("Username", username);
            table.AddRow(
                "Access token expires in",
                FormatExpiry(expiresIn));
            AnsiConsole.Write(table);
        }

        return ExitCodes.Ok;
    }

    private static string FormatExpiry(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "(expired)";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        if (remaining.TotalMinutes >= 1) return $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s";
        return $"{(int)remaining.TotalSeconds}s";
    }
}
