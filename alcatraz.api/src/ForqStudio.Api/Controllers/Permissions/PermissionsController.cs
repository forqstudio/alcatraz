using Asp.Versioning;
using ForqStudio.Application.Permissions.CreatePermission;
using ForqStudio.Application.Permissions.DeletePermission;
using ForqStudio.Application.Permissions.GetPermission;
using ForqStudio.Application.Permissions.GetPermissions;
using ForqStudio.Application.Permissions.UpdatePermission;
using ForqStudio.Domain.Users;
using ForqStudio.Infrastructure.Authorization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DomainPermissions = ForqStudio.Domain.Users.Permissions;

namespace ForqStudio.Api.Controllers.Permissions;

[ApiController]
[Authorize]
[ApiVersion(ApiVersions.V1)]
[Route("api/v{version:apiVersion}/permissions")]
public class PermissionsController(ISender sender) : ControllerBase
{
    [HttpGet]
    [HasPermission(DomainPermissions.PermissionsRead)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetPermissionsQuery(), cancellationToken);

        return Ok(result.Value);
    }

    [HttpGet("{id:int}")]
    [HasPermission(DomainPermissions.PermissionsRead)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetPermissionQuery(id), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    [HttpPost]
    [HasPermission(DomainPermissions.PermissionsWrite)]
    public async Task<IActionResult> Create(
        CreatePermissionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CreatePermissionCommand(request.Name), cancellationToken);

        if (result.IsFailure)
        {
            return BadRequest(result.Error);
        }

        return CreatedAtAction(nameof(GetById), new { id = result.Value }, result.Value);
    }

    [HttpPut("{id:int}")]
    [HasPermission(DomainPermissions.PermissionsWrite)]
    public async Task<IActionResult> Update(
        int id,
        UpdatePermissionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new UpdatePermissionCommand(id, request.Name), cancellationToken);

        if (result.IsSuccess)
        {
            return NoContent();
        }

        return result.Error == PermissionErrors.NotFound
            ? NotFound(result.Error)
            : BadRequest(result.Error);
    }

    [HttpDelete("{id:int}")]
    [HasPermission(DomainPermissions.PermissionsWrite)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeletePermissionCommand(id), cancellationToken);

        if (result.IsSuccess)
        {
            return NoContent();
        }

        return result.Error == PermissionErrors.NotFound
            ? NotFound(result.Error)
            : BadRequest(result.Error);
    }
}
