using System.Diagnostics;
using FluentAssertions;
using ForqStudio.Application.Abstractions.Security;
using ForqStudio.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ForqStudio.Api.FunctionalTests.Sandboxes;

public class SshKeygenCertificateAuthorityTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _caPath;
    private readonly string _userPubkey;

    public SshKeygenCertificateAuthorityTests()
    {
        _workDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"alcatraz-ca-test-{Guid.NewGuid():N}")).FullName;
        _caPath = Path.Combine(_workDir, "ca");

        RunSshKeygen("-t ed25519 -f \"" + _caPath + "\" -N \"\" -C alcatraz-test-ca");

        var userKey = Path.Combine(_workDir, "user");
        RunSshKeygen("-t ed25519 -f \"" + userKey + "\" -N \"\" -C alcatraz-test-user");

        _userPubkey = File.ReadAllText(userKey + ".pub").Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task IssueAsync_ReturnsValidCertificate_AcceptedBySshKeygen()
    {
        if (!SshKeygenAvailable())
        {
            return;
        }

        var ca = new SshKeygenCertificateAuthority(
            Options.Create(new SshCertificateAuthorityOptions
            {
                PrivateKeyPath = _caPath,
                DefaultTtlHours = 24,
                SshKeygenPath = "ssh-keygen",
            }),
            NullLogger<SshKeygenCertificateAuthority>.Instance);

        var sandboxId = Guid.NewGuid().ToString();
        var keyId = $"sub:{sandboxId}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        var result = await ca.IssueAsync(
            _userPubkey,
            sandboxId,
            TimeSpan.FromHours(24),
            keyId,
            DateTime.UtcNow);

        result.IsSuccess.Should().BeTrue($"signing should succeed; error: {(result.IsFailure ? result.Error.Code : "")}");
        result.Value.CertOpenSsh.Should().StartWith("ssh-ed25519-cert-v01@openssh.com");

        // Round-trip: write cert to disk and parse with ssh-keygen -L
        var certPath = Path.Combine(_workDir, "user-cert.pub");
        await File.WriteAllTextAsync(certPath, result.Value.CertOpenSsh);

        var (exitCode, stdout, _) = await RunCapture("ssh-keygen", $"-L -f \"{certPath}\"");
        exitCode.Should().Be(0);
        stdout.Should().Contain("Type: ssh-ed25519-cert-v01@openssh.com user certificate");
        stdout.Should().Contain($"Key ID: \"{keyId}\"");
        stdout.Should().Contain($"Principals:");
        stdout.Should().Contain(sandboxId);
    }

    [Fact]
    public async Task IssueAsync_WhenCaKeyMissing_ReturnsFailure()
    {
        var ca = new SshKeygenCertificateAuthority(
            Options.Create(new SshCertificateAuthorityOptions
            {
                PrivateKeyPath = "/nonexistent/path/ca",
                SshKeygenPath = "ssh-keygen",
            }),
            NullLogger<SshKeygenCertificateAuthority>.Instance);

        var result = await ca.IssueAsync(
            _userPubkey,
            "sandbox",
            TimeSpan.FromHours(24),
            "kid",
            DateTime.UtcNow);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SshCertificateErrors.SigningFailed);
    }

    private static bool SshKeygenAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("ssh-keygen", "-V") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
            return p is not null;
        }
        catch
        {
            return false;
        }
    }

    private static void RunSshKeygen(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("ssh-keygen", arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCapture(string fileName, string arguments)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        p.Start();
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, stdout, stderr);
    }
}
