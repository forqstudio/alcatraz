using Asp.Versioning;
using ForqStudio.Application.UserSettings.GetUserSettings;
using ForqStudio.Application.UserSettings.UpdateUserSettings;
using ForqStudio.Domain.Users;
using ForqStudio.Infrastructure.Authorization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DomainPermissions = ForqStudio.Domain.Users.Permissions;

namespace ForqStudio.Api.Controllers.UserSettings;

[ApiController]
[Authorize]
[ApiVersion(ApiVersions.V1)]
[Route("api/v{version:apiVersion}/user-settings")]
public class UserSettingsController(ISender sender) : ControllerBase
{
    [HttpGet]
    [HasPermission(DomainPermissions.UsersRead)]
    public async Task<IActionResult> GetUserSettings(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetUserSettingsQuery(), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    [HttpPut]
    [HasPermission(DomainPermissions.UsersWrite)]
    public async Task<IActionResult> UpdateUserSettings(
        UpdateUserSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var command = new UpdateUserSettingsCommand(
            request.PreferredLanguage,
            request.EmailNotificationsEnabled,
            request.Timezone);

        var result = await sender.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            return NoContent();
        }

        return result.Error == UserSettingsErrors.NotFound
            ? NotFound(result.Error)
            : BadRequest(result.Error);
    }
}
