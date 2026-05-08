using Asp.Versioning;
using ForqStudio.Api.Extensions;
using ForqStudio.Application.Auth.ExchangeDeviceToken;
using ForqStudio.Application.Auth.InitiateDeviceAuth;
using ForqStudio.Application.Auth.RefreshDeviceToken;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ForqStudio.Api.Controllers.Auth;

[ApiController]
[AllowAnonymous]
[ApiVersion(ApiVersions.V1)]
[Route("api/v{version:apiVersion}/auth")]
public class AuthController(ISender sender) : ControllerBase
{
    [HttpPost("device")]
    public async Task<IActionResult> InitiateDevice(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new InitiateDeviceAuthCommand(), cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.Error.ToFailureActionResult();
    }

    [HttpPost("device/token")]
    public async Task<IActionResult> ExchangeDeviceToken(
        ExchangeDeviceTokenRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new ExchangeDeviceTokenCommand(request.DeviceCode),
            cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.Error.ToFailureActionResult();
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshDeviceToken(
        RefreshDeviceTokenRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new RefreshDeviceTokenCommand(request.RefreshToken),
            cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.Error.ToFailureActionResult();
    }
}
