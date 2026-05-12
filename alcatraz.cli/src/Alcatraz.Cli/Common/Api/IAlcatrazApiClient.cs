using Alcatraz.Cli.Commands.Login;
using Alcatraz.Cli.Commands.Sandboxes;
using Alcatraz.Cli.Commands.Sandboxes.IssueSshCertificate;
using Alcatraz.Cli.Commands.Sandboxes.Usage;

namespace Alcatraz.Cli.Common.Api;

public interface IAlcatrazApiClient
{
    Task<DeviceAuthorizationResponse> InitiateDeviceAuthAsync(CancellationToken ct = default);

    Task<DeviceTokenExchangeResult> PollDeviceTokenAsync(string deviceCode, CancellationToken ct = default);

    Task<DeviceTokenResponse> RefreshDeviceTokenAsync(string refreshToken, CancellationToken ct = default);

    Task<SandboxResponse> CreateSandboxAsync(int vcpus, int memoryMib, CancellationToken ct = default);

    Task<IReadOnlyList<SandboxResponse>> ListSandboxesAsync(CancellationToken ct = default);

    Task<SandboxResponse> GetSandboxAsync(Guid id, CancellationToken ct = default);

    Task DeleteSandboxAsync(Guid id, CancellationToken ct = default);

    Task<SshCertificateResponse> IssueSshCertificateAsync(
        Guid sandboxId,
        string sshPublicKey,
        CancellationToken ct = default);

    Task<SandboxUsageResponse> GetSandboxUsageAsync(Guid sandboxId, CancellationToken ct = default);

    Task<IReadOnlyList<SandboxUsageResponse>> ListSandboxUsageAsync(CancellationToken ct = default);
}
