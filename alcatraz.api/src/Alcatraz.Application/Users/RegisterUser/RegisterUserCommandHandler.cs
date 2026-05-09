using Alcatraz.Application.Abstractions.Authentication;
using Alcatraz.Application.Abstractions.Messaging;
using Alcatraz.Domain.Abstractions;
using Alcatraz.Domain.Users;

namespace Alcatraz.Application.Users.RegisterUser;

internal sealed class RegisterUserCommandHandler(
    IAuthenticationService authenticationService,
    IUserRepository userRepository,
    IUnitOfWork unitOfWork
    ) : ICommandHandler<RegisterUserCommand, Guid>
{
    public async Task<Result<Guid>> Handle(
        RegisterUserCommand request,
        CancellationToken cancellationToken)
    {
        var user = User.Create(
            new FirstName(request.FirstName),
            new LastName(request.LastName),
            new Email(request.Email));

        var identityId = await authenticationService.RegisterAsync(
            user,
            request.Password,
            cancellationToken);

        // Register is idempotent at the IDP layer (Keycloak 409 → existing
        // identity). Mirror that here: if the local table already has a row
        // for this identity, return it instead of inserting a duplicate.
        // Covers the orphan case where Keycloak has the user but the local
        // row was lost (e.g. DB volume wiped while Keycloak's wasn't).
        var existing = await userRepository.GetByIdentityIdAsync(identityId, cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        user.SetIdentityId(identityId);

        userRepository.Add(user);

        await unitOfWork.SaveChangesAsync();

        return user.Id;
    }
}