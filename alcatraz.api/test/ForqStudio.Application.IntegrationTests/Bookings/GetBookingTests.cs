using ForqStudio.Application.Bookings.GetBooking;
using ForqStudio.Application.IntegrationTests.Infrastructure;
using ForqStudio.Domain.Bookings;
using FluentAssertions;

namespace ForqStudio.Application.IntegrationTests.Bookings;

public class GetBookingTests : BaseIntegrationTest
{
    private static readonly Guid BookingId = Guid.NewGuid();

    public GetBookingTests(IntegrationTestWebAppFactory factory)
        : base(factory)
    {
    }

    [Fact]
    public async Task GetBooking_ShouldReturnFailure_WhenBookingIsNotFound()
    {
        // Arrange
        var query = new GetBookingQuery(BookingId);

        // Act
        var result = await Sender.Send(query);

        // Assert
        result.Error.Should().Be(BookingErrors.NotFound);
    }
}