using Asp.Versioning;
using Alcatraz.Application.Roles.AssignPermissions;
using Alcatraz.Application.Roles.CreateRole;
using Alcatraz.Application.Roles.DeleteRole;
using Alcatraz.Application.Roles.GetRole;
using Alcatraz.Application.Roles.GetRoles;
using Alcatraz.Application.Roles.RemovePermissions;
using Alcatraz.Application.Roles.UpdateRole;
using Alcatraz.Domain.Users;
using Alcatraz.Infrastructure.Authorization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DomainPermissions = Alcatraz.Domain.Users.Permissions;

namespace Alcatraz.Api.Controllers.Roles;

[ApiController]
[Authorize]
[ApiVersion(ApiVersions.V1)]
[Route("api/v{version:apiVersion}/roles")]
public class RolesController(ISender sender) : ControllerBase
{
    [HttpGet]
    [HasPermission(DomainPermissions.RolesRead)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetRolesQuery(), cancellationToken);

        return Ok(result.Value);
    }

    [HttpGet("{id:int}")]
    [HasPermission(DomainPermissions.RolesRead)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetRoleQuery(id), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : NotFound(result.Error);
    }

    [HttpPost]
    [HasPermission(DomainPermissions.RolesWrite)]
    public async Task<IActionResult> Create(
        CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CreateRoleCommand(request.Name, request.PermissionIds), cancellationToken);

        if (result.IsFailure)
        {
            return BadRequest(result.Error);
        }

        return CreatedAtAction(nameof(GetById), new { id = result.Value }, result.Value);
    }

    [HttpPut("{id:int}")]
    [HasPermission(DomainPermissions.RolesWrite)]
    public async Task<IActionResult> Update(
        int id,
        UpdateRoleRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new UpdateRoleCommand(id, request.Name, request.PermissionIds), cancellationToken);

        if (result.IsSuccess)
        {
            return NoContent();
        }

        return result.Error == RoleErrors.NotFound
            ? NotFound(result.Error)
            : BadRequest(result.Error);
    }

    [HttpDelete("{id:int}")]
    [HasPermission(DomainPermissions.RolesWrite)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteRoleCommand(id), cancellationToken);

        if (result.IsSuccess)
        {
            return NoContent();
        }

        return result.Error == RoleErrors.NotFound
            ? NotFound(result.Error)
            : BadRequest(result.Error);
    }

    [HttpPost("{id:int}/permissions")]
    [HasPermission(DomainPermissions.RolesWrite)]
    public async Task<IActionResult> AssignPermissions(
        int id,
        AssignPermissionsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new AssignPermissionsCommand(id, request.PermissionIds), cancellationToken);

        if (result.IsSuccess)
        {
            return NoContent();
        }

        return result.Error == RoleErrors.NotFound
            ? NotFound(result.Error)
            : BadRequest(result.Error);
    }

    [HttpDelete("{id:int}/permissions")]
    [HasPermission(DomainPermissions.RolesWrite)]
    public async Task<IActionResult> RemovePermissions(
        int id,
        RemovePermissionsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(new RemovePermissionsCommand(id, request.PermissionIds), cancellationToken);

        if (result.IsSuccess)
        {
            return NoContent();
        }

        return result.Error == RoleErrors.NotFound
            ? NotFound(result.Error)
            : BadRequest(result.Error);
    }
}
