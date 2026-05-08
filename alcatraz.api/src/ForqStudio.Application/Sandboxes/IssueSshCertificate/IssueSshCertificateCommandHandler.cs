using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Clock;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Application.Abstractions.Security;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Sandboxes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForqStudio.Application.Sandboxes.IssueSshCertificate;

internal sealed class IssueSshCertificateCommandHandler(
    ISandboxRepository sandboxRepository,
    ISshCertificateAuthority certificateAuthority,
    IUserContext userContext,
    IDateTimeProvider dateTimeProvider,
    IOptions<GatewayOptions> gatewayOptions,
    ILogger<IssueSshCertificateCommandHandler> logger
    ) : ICommandHandler<IssueSshCertificateCommand, SshCertificateResponse>
{
    private static readonly TimeSpan CertificateTtl = TimeSpan.FromHours(24);

    private readonly GatewayOptions gatewayOptions = gatewayOptions.Value;

    public async Task<Result<SshCertificateResponse>> Handle(
        IssueSshCertificateCommand request,
        CancellationToken cancellationToken)
    {
        var sandbox = await sandboxRepository.GetByIdAsync(request.SandboxId, cancellationToken);

        if (sandbox is null)
        {
            return Result.Failure<SshCertificateResponse>(SandboxErrors.NotFound);
        }

        var ownership = sandbox.EnsureOwnedBy(userContext.UserId);
        if (ownership.IsFailure)
        {
            return Result.Failure<SshCertificateResponse>(ownership.Error);
        }

        if (!sandbox.CanIssueCertificate())
        {
            return Result.Failure<SshCertificateResponse>(SandboxErrors.InvalidStateForCertIssue);
        }

        var endpoint = ResolveEndpoint(sandbox);
        if (endpoint is null)
        {
            return Result.Failure<SshCertificateResponse>(SandboxErrors.NotReady);
        }

        var utcNow = dateTimeProvider.UtcNow;
        var unixSeconds = new DateTimeOffset(utcNow, TimeSpan.Zero).ToUnixTimeSeconds();
        var keyId = $"{userContext.IdentityId}:{sandbox.Id}:{unixSeconds}";

        var issued = await certificateAuthority.IssueAsync(
            request.SshPublicKey,
            sandbox.Id.ToString(),
            CertificateTtl,
            keyId,
            utcNow,
            cancellationToken);

        if (issued.IsFailure)
        {
            return Result.Failure<SshCertificateResponse>(issued.Error);
        }

        logger.LogInformation(
            "Issued SSH certificate for sandbox {SandboxId} owner {OwnerUserId} keyId {KeyId} validUntil {ValidUntil}",
            sandbox.Id,
            sandbox.OwnerUserId,
            keyId,
            issued.Value.ValidUntilUtc);

        return new SshCertificateResponse(
            issued.Value.CertOpenSsh,
            issued.Value.ValidUntilUtc,
            endpoint.Value.Host,
            endpoint.Value.Port);
    }

    // Production deployments configure Gateway:Host/Port pointing at the public Traefik
    // ingress; the cert response then routes the customer through it. Local dev compose
    // leaves Gateway empty and the per-sandbox endpoint reported by the worker is used,
    // which the CLI dials directly on the bridge subnet.
    private (string Host, int Port)? ResolveEndpoint(Sandbox sandbox)
    {
        if (!string.IsNullOrWhiteSpace(gatewayOptions.Host) && gatewayOptions.Port > 0)
        {
            return (gatewayOptions.Host, gatewayOptions.Port);
        }

        if (!string.IsNullOrWhiteSpace(sandbox.Host) && sandbox.Port is > 0)
        {
            return (sandbox.Host, sandbox.Port.Value);
        }

        return null;
    }
}
