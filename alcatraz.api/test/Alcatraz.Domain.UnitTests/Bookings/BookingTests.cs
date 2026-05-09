using Alcatraz.Domain.Bookings;
using Alcatraz.Domain.Bookings.Events;
using Alcatraz.Domain.Shared;
using Alcatraz.Domain.UnitTests.Apartments;
using Alcatraz.Domain.UnitTests.Infrastructure;
using Alcatraz.Domain.UnitTests.Users;
using Alcatraz.Domain.Users;
using FluentAssertions;

namespace Alcatraz.Domain.UnitTests.Bookings;

public class BookingTests : BaseTest
{
    [Fact]
    public void Reserve_Should_RaiseBookingReservedDomainEvent()
    {
        // Arrange
        var user = User.Create(UserData.FirstName, UserData.LastName, UserData.Email);
        var price = new Money(10.0m, Currency.USD);
        var duration = DateRange.Create(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 10));
        var apartment = ApartmentData.Create(price);
        var pricingService = new PricingService();

        // Act
        var booking = Booking.Reserve(apartment, user.Id, duration, DateTime.UtcNow, pricingService);

        // Assert
        var bookingReservedDomainEvent = AssertDomainEventWasPublished<BookingReservedDomainEvent>(booking);

        bookingReservedDomainEvent.BookingId.Should().Be(booking.Id);
    }
}
