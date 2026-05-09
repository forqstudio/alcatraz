using System.Diagnostics;
using Alcatraz.Application.Abstractions.Security;
using Alcatraz.Domain.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alcatraz.Infrastructure.Security;

internal sealed class SshKeygenCertificateAuthority(
    IOptions<SshCertificateAuthorityOptions> options,
    ILogger<SshKeygenCertificateAuthority> logger
    ) : ISshCertificateAuthority
{
    private readonly SshCertificateAuthorityOptions caOptions = options.Value;

    public async Task<Result<IssuedSshCertificate>> IssueAsync(
        string sshPublicKeyOpenSsh,
        string principal,
        TimeSpan ttl,
        string keyId,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caOptions.PrivateKeyPath) || !File.Exists(caOptions.PrivateKeyPath))
        {
            logger.LogError(
                "SSH CA private key not found at {Path}",
                caOptions.PrivateKeyPath);
            return Result.Failure<IssuedSshCertificate>(SshCertificateErrors.SigningFailed);
        }

        var workDir = Path.Combine(Path.GetTempPath(), $"alcatraz-ssh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        var pubKeyPath = Path.Combine(workDir, "user.pub");
        var certPath = Path.Combine(workDir, "user-cert.pub");

        try
        {
            await File.WriteAllTextAsync(pubKeyPath, sshPublicKeyOpenSsh.Trim() + "\n", cancellationToken);

            var ttlMinutes = (int)Math.Max(1, Math.Round(ttl.TotalMinutes));

            var startInfo = new ProcessStartInfo
            {
                FileName = caOptions.SshKeygenPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("-s");
            startInfo.ArgumentList.Add(caOptions.PrivateKeyPath);
            startInfo.ArgumentList.Add("-I");
            startInfo.ArgumentList.Add(keyId);
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add(principal);
            startInfo.ArgumentList.Add("-V");
            startInfo.ArgumentList.Add($"+{ttlMinutes}m");
            startInfo.ArgumentList.Add(pubKeyPath);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                logger.LogError(
                    "ssh-keygen exited with code {ExitCode}; stderr: {Stderr}; stdout: {Stdout}",
                    process.ExitCode,
                    stderr,
                    stdout);
                return Result.Failure<IssuedSshCertificate>(SshCertificateErrors.SigningFailed);
            }

            if (!File.Exists(certPath))
            {
                logger.LogError(
                    "ssh-keygen returned 0 but did not produce certificate at {CertPath}; stderr: {Stderr}",
                    certPath,
                    stderr);
                return Result.Failure<IssuedSshCertificate>(SshCertificateErrors.SigningFailed);
            }

            var certContents = (await File.ReadAllTextAsync(certPath, cancellationToken)).Trim();

            return new IssuedSshCertificate(
                certContents,
                utcNow,
                utcNow.Add(ttl));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unexpected error during ssh-keygen invocation");
            return Result.Failure<IssuedSshCertificate>(SshCertificateErrors.SigningFailed);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean up ssh-keygen working directory {WorkDir}", workDir);
            }
        }
    }
}
