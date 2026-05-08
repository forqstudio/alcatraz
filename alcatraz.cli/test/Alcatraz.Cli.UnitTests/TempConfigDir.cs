namespace Alcatraz.Cli.UnitTests;

/// Redirects ConfigPathResolver to a fresh temp directory for the lifetime of the
/// fixture by setting XDG_CONFIG_HOME on POSIX or APPDATA on Windows. Disposed
/// at the end of the test scope; restores the previous env var values.
public sealed class TempConfigDir : IDisposable
{
    private readonly string envName;
    private readonly string? previousValue;

    public string Root { get; }

    public TempConfigDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "alcatraz-cli-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(Root);

        if (OperatingSystem.IsWindows())
        {
            envName = "APPDATA";
            previousValue = Environment.GetEnvironmentVariable(envName);
            Environment.SetEnvironmentVariable(envName, Root);
        }
        else
        {
            envName = "XDG_CONFIG_HOME";
            previousValue = Environment.GetEnvironmentVariable(envName);
            Environment.SetEnvironmentVariable(envName, Root);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(envName, previousValue);
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
