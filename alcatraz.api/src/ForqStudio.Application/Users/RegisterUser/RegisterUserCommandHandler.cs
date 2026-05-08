using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;

namespace ForqStudio.Application.Users.RegisterUser;

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

        user.SetIdentityId(identityId);

        userRepository.Add(user);

        await unitOfWork.SaveChangesAsync();

        return user.Id;
    }
}