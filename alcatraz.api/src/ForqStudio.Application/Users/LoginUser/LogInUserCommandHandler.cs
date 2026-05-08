using ForqStudio.Application.Abstractions.Authentication;
using ForqStudio.Application.Abstractions.Messaging;
using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users;

namespace ForqStudio.Application.Users.LogInUser;

internal sealed class LogInUserCommandHandler(IJwtService jwtService) : ICommandHandler<LogInUserCommand, AccessTokenResponse>
{
    public async Task<Result<AccessTokenResponse>> Handle(
        LogInUserCommand request,
        CancellationToken cancellationToken)
    {
        var result = await jwtService.GetAccessTokenAsync(
            request.Email,
            request.Password,
            cancellationToken);

        if (result.IsFailure)
        {
            return Result.Failure<AccessTokenResponse>(UserErrors.InvalidCredentials);
        }

        return new AccessTokenResponse(result.Value);
    }
}