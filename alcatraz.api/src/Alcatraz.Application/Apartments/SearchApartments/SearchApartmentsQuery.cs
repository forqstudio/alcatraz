using Alcatraz.Application.Abstractions.Messaging;

namespace Alcatraz.Application.Apartments.SearchApartments;

public sealed record SearchApartmentsQuery(DateOnly StartDate, DateOnly EndDate) : IQuery<IReadOnlyList<ApartmentResponse>>;

