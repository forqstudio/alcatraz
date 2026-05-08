using FluentAssertions;
using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Clock;
using ForqStudio.Application.Abstractions.Security;
using ForqStudio.Application.Sandboxes.IssueSshCertificate;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Sandboxes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ForqStudio.Application.UnitTests.Sandboxes;

public class IssueSshCertificateCommandHandlerTests
{
    private static readonly DateTime UtcNow = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid OwnerUserId = Guid.NewGuid();
    private const string IdentityId = "keycloak-sub-123";
    private const string Pubkey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIH9vYJ user@host";
    private const string VmHost = "172.16.0.10";
    private const int VmPort = 22;

    private readonly ISandboxRepository _sandboxRepository = Substitute.For<ISandboxRepository>();
    private readonly ISshCertificateAuthority _ca = Substitute.For<ISshCertificateAuthority>();
    private readonly IUserContext _userContext = Substitute.For<IUserContext>();
    private readonly IDateTimeProvider _clock = Substitute.For<IDateTimeProvider>();

    public IssueSshCertificateCommandHandlerTests()
    {
        _clock.UtcNow.Returns(UtcNow);
        _userContext.UserId.Returns(OwnerUserId);
        _userContext.IdentityId.Returns(IdentityId);
    }

    private IssueSshCertificateCommandHandler BuildHandler(GatewayOptions? gateway = null) =>
        new(
            _sandboxRepository,
            _ca,
            _userContext,
            _clock,
            Options.Create(gateway ?? new GatewayOptions()),
            NullLogger<IssueSshCertificateCommandHandler>.Instance);

    private static Sandbox RunningSandbox(Guid ownerUserId, string host = VmHost, int port = VmPort)
    {
        var sandbox = Sandbox.Request(ownerUserId, 2, 2048, UtcNow);
        sandbox.MarkRunning(host, port, UtcNow);
        return sandbox;
    }

    [Fact]
    public async Task Handle_WhenSandboxMissing_ReturnsNotFound()
    {
        var sandboxId = Guid.NewGuid();
        _sandboxRepository.GetByIdAsync(sandboxId, Arg.Any<CancellationToken>()).Returns((Sandbox?)null);

        var result = await BuildHandler().Handle(new IssueSshCertificateCommand(sandboxId, Pubkey), CancellationToken.None);

        result.Error.Should().Be(SandboxErrors.NotFound);
    }

    [Fact]
    public async Task Handle_WhenOwnedByOtherUser_ReturnsNotFound()
    {
        var sandbox = RunningSandbox(Guid.NewGuid());
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await BuildHandler().Handle(new IssueSshCertificateCommand(sandbox.Id, Pubkey), CancellationToken.None);

        result.Error.Should().Be(SandboxErrors.NotFound);
    }

    [Fact]
    public async Task Handle_WhenSandboxStillProvisioning_ReturnsInvalidState()
    {
        // Cert issuance now requires Running — a worker must have published vm.ready first.
        var sandbox = Sandbox.Request(OwnerUserId, 2, 2048, UtcNow);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await BuildHandler().Handle(new IssueSshCertificateCommand(sandbox.Id, Pubkey), CancellationToken.None);

        result.Error.Should().Be(SandboxErrors.InvalidStateForCertIssue);
    }

    [Fact]
    public async Task Handle_WhenSandboxDeleting_ReturnsInvalidState()
    {
        var sandbox = RunningSandbox(OwnerUserId);
        sandbox.MarkDeleting(UtcNow);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var result = await BuildHandler().Handle(new IssueSshCertificateCommand(sandbox.Id, Pubkey), CancellationToken.None);

        result.Error.Should().Be(SandboxErrors.InvalidStateForCertIssue);
    }

    [Fact]
    public async Task Handle_GatewayConfigured_ReturnsGatewayEndpoint()
    {
        var sandbox = RunningSandbox(OwnerUserId);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var validUntil = UtcNow.AddHours(24);
        _ca.IssueAsync(
                Pubkey,
                sandbox.Id.ToString(),
                Arg.Any<TimeSpan>(),
                Arg.Any<string>(),
                UtcNow,
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(new IssuedSshCertificate("cert-blob", UtcNow, validUntil)));

        var handler = BuildHandler(new GatewayOptions { Host = "ssh.alcatraz.io", Port = 443 });

        var result = await handler.Handle(new IssueSshCertificateCommand(sandbox.Id, Pubkey), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Cert.Should().Be("cert-blob");
        result.Value.ValidUntilUtc.Should().Be(validUntil);
        result.Value.GatewayHost.Should().Be("ssh.alcatraz.io");
        result.Value.GatewayPort.Should().Be(443);

        await _ca.Received(1).IssueAsync(
            Pubkey,
            sandbox.Id.ToString(),
            TimeSpan.FromHours(24),
            Arg.Is<string>(k => k.StartsWith($"{IdentityId}:{sandbox.Id}:", StringComparison.Ordinal)),
            UtcNow,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_GatewayNotConfigured_ReturnsPerSandboxEndpoint()
    {
        var sandbox = RunningSandbox(OwnerUserId);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        var validUntil = UtcNow.AddHours(24);
        _ca.IssueAsync(
                Pubkey,
                sandbox.Id.ToString(),
                Arg.Any<TimeSpan>(),
                Arg.Any<string>(),
                UtcNow,
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(new IssuedSshCertificate("cert-blob", UtcNow, validUntil)));

        // No gateway configured -> handler returns the worker-reported endpoint.
        var result = await BuildHandler().Handle(new IssueSshCertificateCommand(sandbox.Id, Pubkey), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GatewayHost.Should().Be(VmHost);
        result.Value.GatewayPort.Should().Be(VmPort);
    }

    [Fact]
    public async Task Handle_WhenCAFails_PropagatesError()
    {
        var sandbox = RunningSandbox(OwnerUserId);
        _sandboxRepository.GetByIdAsync(sandbox.Id, Arg.Any<CancellationToken>()).Returns(sandbox);

        _ca.IssueAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<string>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IssuedSshCertificate>(SshCertificateErrors.SigningFailed));

        var result = await BuildHandler().Handle(new IssueSshCertificateCommand(sandbox.Id, Pubkey), CancellationToken.None);

        result.Error.Should().Be(SshCertificateErrors.SigningFailed);
    }
}
