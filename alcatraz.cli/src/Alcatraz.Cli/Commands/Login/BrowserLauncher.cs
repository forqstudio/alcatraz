using System.Diagnostics;

namespace Alcatraz.Cli.Commands.Login;

public interface IBrowserLauncher
{
    bool TryOpen(string url);
}

internal sealed class BrowserLauncher : IBrowserLauncher
{
    public bool TryOpen(string url)
    {
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo(url) { UseShellExecute = true };
            }
            else if (OperatingSystem.IsMacOS())
            {
                psi = new ProcessStartInfo("open", [url]) { UseShellExecute = false };
            }
            else
            {
                psi = new ProcessStartInfo("xdg-open", [url]) { UseShellExecute = false };
            }

            using var p = Process.Start(psi);
            return p is not null;
        }
        catch
        {
            return false;
        }
    }
}
