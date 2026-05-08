using Asp.Versioning;
using ForqStudio.Application.Users.GetLoggedInUser;
using ForqStudio.Application.Users.LogInUser;
using ForqStudio.Application.Users.RegisterUser;
using ForqStudio.Domain.Users;
using ForqStudio.Infrastructure.Authorization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DomainPermissions = ForqStudio.Domain.Users.Permissions;

namespace ForqStudio.Api.Controllers.Users;

[ApiController]
[ApiVersion(ApiVersions.V1)]
[ApiVersion(ApiVersions.V2)]
[Route("api/v{version:apiVersion}/users")]
public class UsersController(ISender sender) : ControllerBase
{
    [AllowAnonymous]
    [ApiVersion(ApiVersions.V1)]
    [HttpPost("register")]
    public async Task<IActionResult> Register(
        RegisterUserRequest request,
        CancellationToken cancellationToken)
    {
        var command = new RegisterUserCommand(
            request.Email,
            request.FirstName,
            request.LastName,
            request.Password);

        var result = await sender.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            return BadRequest(result.Error);
        }

        return Ok(result.Value);
    }

    [AllowAnonymous]
    [ApiVersion(ApiVersions.V1)]
    [HttpPost("login")]
    public async Task<IActionResult> LogIn(
        LogInUserRequest request,
        CancellationToken cancellationToken)
    {
        var command = new LogInUserCommand(request.Email, request.Password);

        var result = await sender.Send(command, cancellationToken);

        if (result.IsFailure)
        {
            return Unauthorized(result.Error);
        }

        return Ok(result.Value);
    }

    [HttpGet("me")]
    [ApiVersion(ApiVersions.V1)]
    [HasPermission(DomainPermissions.UsersRead)]
    public async Task<IActionResult> GetLoggedInUserV1(CancellationToken cancellationToken)
    {
        var query = new GetLoggedInUserQuery();

        var result = await sender.Send(query, cancellationToken);

        return Ok(result.Value);
    }

    [HttpGet("me")]
    [MapToApiVersion(ApiVersions.V2)]
    [HasPermission(DomainPermissions.UsersRead)]
    public async Task<IActionResult> GetLoggedInUserV2(CancellationToken cancellationToken)
    {
        var query = new GetLoggedInUserQuery();

        var result = await sender.Send(query, cancellationToken);

        return Ok(result.Value);
    }
}
