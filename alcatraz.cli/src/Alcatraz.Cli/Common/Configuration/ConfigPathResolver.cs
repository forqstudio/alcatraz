namespace Alcatraz.Cli.Common.Configuration;

internal static class ConfigPathResolver
{
    public static string GetConfigDir()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "alcatraz");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = !string.IsNullOrWhiteSpace(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "alcatraz");
    }

    public static string GetConfigFile() => Path.Combine(GetConfigDir(), "config.json");

    public static string GetTokensFile() => Path.Combine(GetConfigDir(), "tokens.json");

    public static string GetPrivateKeyPath() => Path.Combine(GetConfigDir(), "id_alcatraz");

    public static string GetPublicKeyPath() => Path.Combine(GetConfigDir(), "id_alcatraz.pub");

    public static string GetCertCacheDir() => Path.Combine(GetConfigDir(), "certs");

    public static string GetCertPath(Guid sandboxId) =>
        Path.Combine(GetCertCacheDir(), $"{sandboxId}-cert.pub");

    public static string GetCertSidecarPath(Guid sandboxId) =>
        Path.Combine(GetCertCacheDir(), $"{sandboxId}-cert.json");

    public static string GetKnownHostsPath() => Path.Combine(GetConfigDir(), "known_hosts");

    public static void EnsureDir()
    {
        var dir = GetConfigDir();
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
