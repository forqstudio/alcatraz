using System.Text.Json;
using Alcatraz.Cli.Common.Configuration;

namespace Alcatraz.Cli.Common.Authentication;

public interface ITokenStore
{
    Task<StoredTokens?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(StoredTokens tokens, CancellationToken ct = default);
    void Clear();
}

internal sealed class TokenStore : ITokenStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task<StoredTokens?> LoadAsync(CancellationToken ct = default)
    {
        var path = ConfigPathResolver.GetTokensFile();
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StoredTokens>(stream, Json, ct);
    }

    public async Task SaveAsync(StoredTokens tokens, CancellationToken ct = default)
    {
        ConfigPathResolver.EnsureDir();
        var path = ConfigPathResolver.GetTokensFile();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(tokens, Json), ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Clear()
    {
        var path = ConfigPathResolver.GetTokensFile();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
