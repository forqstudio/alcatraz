using Alcatraz.Cli.Common.Configuration;
using Alcatraz.Cli.Common.Ssh;
using FluentAssertions;

namespace Alcatraz.Cli.UnitTests.Common.Ssh;

[Collection("ConfigPath")]
public class CertificateCacheTests
{
    private readonly FakeClock clock = new(new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void TryGetValidCert_NoFile_ReturnsFalse()
    {
        using var dir = new TempConfigDir();
        var cache = new CertificateCache(clock);

        var has = cache.TryGetValidCert(Guid.NewGuid(), TimeSpan.FromMinutes(5), out _, out _);

        has.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ThenTryGet_Hit()
    {
        using var dir = new TempConfigDir();
        var cache = new CertificateCache(clock);
        var id = Guid.NewGuid();

        await cache.SaveAsync(id, "ssh-cert-body", clock.GetUtcNow().AddHours(24).UtcDateTime);
        var has = cache.TryGetValidCert(id, TimeSpan.FromMinutes(5), out var path, out var expiresUtc);

        has.Should().BeTrue();
        path.Should().Be(ConfigPathResolver.GetCertPath(id));
        expiresUtc.Should().Be(clock.GetUtcNow().AddHours(24).UtcDateTime);
    }

    [Fact]
    public async Task TryGetValidCert_WithinSafetyMargin_ReturnsFalse()
    {
        using var dir = new TempConfigDir();
        var cache = new CertificateCache(clock);
        var id = Guid.NewGuid();
        await cache.SaveAsync(id, "body", clock.GetUtcNow().AddMinutes(2).UtcDateTime);

        var has = cache.TryGetValidCert(id, TimeSpan.FromMinutes(5), out _, out _);

        has.Should().BeFalse();
    }

    private sealed class FakeClock(DateTime initial) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(initial, TimeSpan.Zero);
    }
}
