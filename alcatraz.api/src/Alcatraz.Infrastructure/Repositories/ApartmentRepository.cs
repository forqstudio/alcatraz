using Alcatraz.Domain.Apartments;

namespace Alcatraz.Infrastructure.Repositories;

internal sealed class ApartmentRepository(ApplicationDbContext dbContext)
    : Repository<Apartment>(dbContext), IApartmentRepository
{
}
