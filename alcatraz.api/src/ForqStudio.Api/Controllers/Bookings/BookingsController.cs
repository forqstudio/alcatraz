using Asp.Versioning;
using ForqStudio.Application.Bookings.GetBooking;
using ForqStudio.Application.Bookings.ReserveBooking;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace ForqStudio.Api.Controllers.Bookings;

    [ApiController]
    [Authorize]
    [ApiVersion(ApiVersions.V1)]
    [Route("api/v{version:apiVersion}/bookings")]
    public class BookingsController(ISender sender) : ControllerBase
    {
        [HttpGet("{id}")]
        public async Task<IActionResult> GetBooking(
            Guid id,
            CancellationToken cancellationToken)
        {
            var result = await sender.Send(new GetBookingQuery(id), cancellationToken);
            return result.IsSuccess ? Ok(result.Value) : NotFound();
        }

        [HttpPost]
        public async Task<IActionResult> ReserveBooking(
            ReserveBookingRequest request,
            CancellationToken cancellationToken)
        {
            var command = new ReserveBookingCommand(
                request.ApartmentId,
                request.UserId,
                request.StartDate,
                request.EndDate);

            var result = await sender.Send(command, cancellationToken);

            if (result.IsFailure)
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, result.Error);
            }

            return CreatedAtAction(nameof(GetBooking), new { id = result.Value }, result.Value);
        }
    }
