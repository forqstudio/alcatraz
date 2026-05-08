using Alcatraz.Cli.Common.Configuration;
using FluentAssertions;

namespace Alcatraz.Cli.UnitTests.Common.Configuration;

public class ConfigPathResolverTests
{
    [Fact]
    public void GetConfigDir_LinuxXdgSet_UsesXdg()
    {
        if (OperatingSystem.IsWindows()) return;
        var prev = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var tmp = Path.Combine(Path.GetTempPath(), "xdg-test-" + Guid.NewGuid());
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", tmp);

            var dir = ConfigPathResolver.GetConfigDir();

            dir.Should().Be(Path.Combine(tmp, "alcatraz"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prev);
        }
    }

    [Fact]
    public void GetConfigDir_LinuxXdgUnset_UsesHomeDotConfig()
    {
        if (OperatingSystem.IsWindows()) return;
        var prev = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);

            var dir = ConfigPathResolver.GetConfigDir();

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dir.Should().Be(Path.Combine(home, ".config", "alcatraz"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prev);
        }
    }

    [Fact]
    public void GetCertPath_FormatsAsExpected()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var path = ConfigPathResolver.GetCertPath(id);

        path.Should().EndWith($"{id}-cert.pub");
    }
}
