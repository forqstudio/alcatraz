using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Clock;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Sandboxes;

namespace Alcatraz.Application.Sandboxes.CreateSandbox;

internal sealed class CreateSandboxCommandHandler(
    ISandboxRepository sandboxRepository,
    IUserContext userContext,
    IUnitOfWork unitOfWork,
    IDateTimeProvider dateTimeProvider
    ) : ICommandHandler<CreateSandboxCommand, SandboxResponse>
{
    public async Task<Result<SandboxResponse>> Handle(
        CreateSandboxCommand request,
        CancellationToken cancellationToken)
    {
        var sandbox = Sandbox.Request(
            userContext.UserId,
            request.Vcpus,
            request.MemoryMib,
            dateTimeProvider.UtcNow);

        sandboxRepository.Add(sandbox);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new SandboxResponse
        {
            Id = sandbox.Id,
            OwnerUserId = sandbox.OwnerUserId,
            Vcpus = sandbox.RequestedVcpus,
            MemoryMib = sandbox.RequestedMemoryMib,
            Status = (int)sandbox.Status,
            CreatedOnUtc = sandbox.CreatedOnUtc,
            DeletedOnUtc = sandbox.DeletedOnUtc,
        };
    }
}
