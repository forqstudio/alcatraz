using Asp.Versioning;
using ForqStudio.Api.Extensions;
using ForqStudio.Application.Sandboxes.CreateSandbox;
using ForqStudio.Application.Sandboxes.DeleteSandbox;
using ForqStudio.Application.Sandboxes.GetSandbox;
using ForqStudio.Application.Sandboxes.IssueSshCertificate;
using ForqStudio.Application.Sandboxes.ListSandboxes;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ForqStudio.Api.Controllers.Sandboxes;

[ApiController]
[Authorize]
[ApiVersion(ApiVersions.V1)]
[Route("api/v{version:apiVersion}/sandboxes")]
public class SandboxesController(ISender sender) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> CreateSandbox(
        CreateSandboxRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new CreateSandboxCommand(request.Vcpus, request.MemoryMib),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error.ToFailureActionResult();
        }

        return CreatedAtAction(
            nameof(GetSandbox),
            new { id = result.Value.Id },
            result.Value);
    }

    [HttpGet]
    public async Task<IActionResult> ListSandboxes(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ListSandboxesQuery(), cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.Error.ToFailureActionResult();
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetSandbox(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetSandboxQuery(id), cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.Error.ToFailureActionResult();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteSandbox(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteSandboxCommand(id), cancellationToken);

        return result.IsSuccess
            ? Accepted()
            : result.Error.ToFailureActionResult();
    }

    [HttpPost("{id:guid}/ssh-cert")]
    public async Task<IActionResult> IssueSshCertificate(
        Guid id,
        IssueSshCertificateRequest request,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new IssueSshCertificateCommand(id, request.SshPublicKey),
            cancellationToken);

        return result.IsSuccess
            ? Ok(result.Value)
            : result.Error.ToFailureActionResult();
    }
}
