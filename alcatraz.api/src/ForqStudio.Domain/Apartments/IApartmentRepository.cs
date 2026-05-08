namespace ForqStudio.Domain.Apartments;

public interface IApartmentRepository
{
    Task<Apartment?> GetByIdAsync(Guid id, CancellationToken cancellation = default);
}
