using ForqStudio.Domain.Apartments;

namespace ForqStudio.Infrastructure.Repositories;

internal sealed class ApartmentRepository(ApplicationDbContext dbContext)
    : Repository<Apartment>(dbContext), IApartmentRepository
{
}
