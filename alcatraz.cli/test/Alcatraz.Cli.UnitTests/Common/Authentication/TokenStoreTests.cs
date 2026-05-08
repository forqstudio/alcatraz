using Alcatraz.Cli.Common.Authentication;
using Alcatraz.Cli.Common.Configuration;
using FluentAssertions;

namespace Alcatraz.Cli.UnitTests.Common.Authentication;

[Collection("ConfigPath")]
public class TokenStoreTests
{
    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsNull()
    {
        using var _ = new TempConfigDir();
        var store = new TokenStore();

        var loaded = await store.LoadAsync();

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTrips()
    {
        using var _ = new TempConfigDir();
        var store = new TokenStore();
        var tokens = new StoredTokens(
            "access",
            "refresh",
            new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "Bearer",
            "id-token");

        await store.SaveAsync(tokens);
        var loaded = await store.LoadAsync();

        loaded.Should().Be(tokens);
    }

    [Fact]
    public async Task SaveAsync_OnPosix_WritesFileMode0600()
    {
        if (OperatingSystem.IsWindows()) return;

        using var _ = new TempConfigDir();
        var store = new TokenStore();
        await store.SaveAsync(new StoredTokens("a", "r", DateTime.UtcNow, "Bearer", null));

        var path = ConfigPathResolver.GetTokensFile();
        var mode = File.GetUnixFileMode(path);

        mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task Clear_RemovesFile()
    {
        using var _ = new TempConfigDir();
        var store = new TokenStore();
        await store.SaveAsync(new StoredTokens("a", "r", DateTime.UtcNow, "Bearer", null));

        store.Clear();

        File.Exists(ConfigPathResolver.GetTokensFile()).Should().BeFalse();
    }
}
