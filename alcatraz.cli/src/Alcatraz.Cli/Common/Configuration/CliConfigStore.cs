using System.Text.Json;

namespace Alcatraz.Cli.Common.Configuration;

public interface ICliConfigStore
{
    CliConfig Load();
    void Save(CliConfig config);
}

internal sealed class CliConfigStore : ICliConfigStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public CliConfig Load()
    {
        var path = ConfigPathResolver.GetConfigFile();
        if (!File.Exists(path))
        {
            return CliConfig.Default;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CliConfig>(json, Json) ?? CliConfig.Default;
    }

    public void Save(CliConfig config)
    {
        ConfigPathResolver.EnsureDir();
        var path = ConfigPathResolver.GetConfigFile();
        File.WriteAllText(path, JsonSerializer.Serialize(config, Json));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
