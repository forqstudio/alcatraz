using Alcatraz.Cli.Common.Configuration;
using FluentAssertions;

namespace Alcatraz.Cli.UnitTests.Common.Configuration;

[Collection("ConfigPath")]
public class CliConfigStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        using var _ = new TempConfigDir();
        var store = new CliConfigStore();

        var loaded = store.Load();

        loaded.Should().Be(CliConfig.Default);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        using var _ = new TempConfigDir();
        var store = new CliConfigStore();
        var cfg = new CliConfig("https://api.example.com", true);

        store.Save(cfg);
        var loaded = store.Load();

        loaded.Should().Be(cfg);
    }
}
