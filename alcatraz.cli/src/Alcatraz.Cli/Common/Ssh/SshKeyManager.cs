using System.Diagnostics;
using Alcatraz.Cli.Common.Configuration;

namespace Alcatraz.Cli.Common.Ssh;

public interface ISshKeyManager
{
    Task<(string PrivatePath, string PublicPath)> EnsureKeyPairAsync(CancellationToken ct = default);
    Task<string> ReadPublicKeyAsync(string path, CancellationToken ct = default);
}

internal sealed class SshKeyManager : ISshKeyManager
{
    public async Task<(string PrivatePath, string PublicPath)> EnsureKeyPairAsync(CancellationToken ct = default)
    {
        ConfigPathResolver.EnsureDir();
        var priv = ConfigPathResolver.GetPrivateKeyPath();
        var pub = ConfigPathResolver.GetPublicKeyPath();

        if (File.Exists(priv) && File.Exists(pub))
        {
            return (priv, pub);
        }

        // ssh-keygen refuses if the file already exists; clean any half-state.
        if (File.Exists(priv)) File.Delete(priv);
        if (File.Exists(pub)) File.Delete(pub);

        var psi = new ProcessStartInfo("ssh-keygen")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("ed25519");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(priv);
        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add(string.Empty);
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add("alcatraz-cli");

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ssh-keygen");
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"ssh-keygen failed: {stderr}");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(priv, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(pub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return (priv, pub);
    }

    public async Task<string> ReadPublicKeyAsync(string path, CancellationToken ct = default) =>
        (await File.ReadAllTextAsync(path, ct)).Trim();
}
