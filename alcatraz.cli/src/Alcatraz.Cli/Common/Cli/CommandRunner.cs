using Alcatraz.Cli.Common.Api;
using Alcatraz.Cli.Common.Bootstrap;
using Spectre.Console;

namespace Alcatraz.Cli.Common.Cli;

internal sealed class CommandRunner(CancellationContext cancellation)
{
    public async Task<int> RunAsync(Func<CancellationToken, Task<int>> action)
    {
        var ct = cancellation.Token;
        try
        {
            return await action(ct);
        }
        catch (NotLoggedInException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]{ex.Message}[/]");
            return ExitCodes.NotLoggedIn;
        }
        catch (SandboxNotFoundException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return ExitCodes.NotFound;
        }
        catch (ConflictException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return ExitCodes.Conflict;
        }
        catch (BadRequestException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return ExitCodes.InvalidArgs;
        }
        catch (ApiUnavailableException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return ExitCodes.Generic;
        }
        catch (AlcatrazCliException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{ex.Message}[/]");
            return ExitCodes.Generic;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ExitCodes.Generic;
        }
    }
}
