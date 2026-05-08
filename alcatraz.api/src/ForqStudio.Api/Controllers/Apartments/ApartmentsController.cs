using Asp.Versioning;
using ForqStudio.Application.Apartments.SearchApartments;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ForqStudio.Api.Controllers.Apartments;

[ApiController]
[Authorize]
[ApiVersion(ApiVersions.V1)]
[Route("api/v{version:apiVersion}/apartments")]

public class ApartmentsController(ISender sender) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> SearchApartments(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        var query = new SearchApartmentsQuery(startDate, endDate);

        var result = await sender.Send(query, cancellationToken);

        return Ok(result.Value);
    }
}

