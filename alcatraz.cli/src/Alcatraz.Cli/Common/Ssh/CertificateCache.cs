using System.Text.Json;
using Alcatraz.Cli.Common.Configuration;

namespace Alcatraz.Cli.Common.Ssh;

public interface ICertificateCache
{
    bool TryGetValidCert(Guid sandboxId, TimeSpan minRemaining, out string path, out DateTime expiresUtc);
    Task SaveAsync(Guid sandboxId, string certBody, DateTime validUntilUtc, CancellationToken ct = default);
}

internal sealed class CertificateCache(TimeProvider clock) : ICertificateCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool TryGetValidCert(
        Guid sandboxId,
        TimeSpan minRemaining,
        out string path,
        out DateTime expiresUtc)
    {
        path = ConfigPathResolver.GetCertPath(sandboxId);
        expiresUtc = default;

        if (!File.Exists(path))
        {
            return false;
        }

        var sidecar = ConfigPathResolver.GetCertSidecarPath(sandboxId);
        if (!File.Exists(sidecar))
        {
            return false;
        }

        try
        {
            var meta = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(sidecar), Json);
            if (meta is null) return false;
            expiresUtc = meta.ValidUntilUtc;
        }
        catch
        {
            return false;
        }

        return expiresUtc - clock.GetUtcNow().UtcDateTime >= minRemaining;
    }

    public async Task SaveAsync(
        Guid sandboxId,
        string certBody,
        DateTime validUntilUtc,
        CancellationToken ct = default)
    {
        var dir = ConfigPathResolver.GetCertCacheDir();
        Directory.CreateDirectory(dir);

        var certPath = ConfigPathResolver.GetCertPath(sandboxId);
        var sidecarPath = ConfigPathResolver.GetCertSidecarPath(sandboxId);

        await File.WriteAllTextAsync(certPath, certBody, ct);
        await File.WriteAllTextAsync(
            sidecarPath,
            JsonSerializer.Serialize(new Sidecar(validUntilUtc), Json),
            ct);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(certPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(sidecarPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed record Sidecar(DateTime ValidUntilUtc);
}
