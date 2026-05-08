using ForqStudio.Application.Abstractions.Messaging;

namespace ForqStudio.Application.Sandboxes.IssueSshCertificate;

public sealed record IssueSshCertificateCommand(Guid SandboxId, string SshPublicKey)
    : ICommand<SshCertificateResponse>;
