using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Sandboxes.IssueSshCertificate;

public sealed record IssueSshCertificateCommand(Guid SandboxId, string SshPublicKey)
    : ICommand<SshCertificateResponse>;
