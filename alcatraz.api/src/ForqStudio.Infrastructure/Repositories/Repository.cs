using ForqStudio.Domain.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ForqStudio.Infrastructure.Repositories;

internal abstract class Repository<T>(ApplicationDbContext dbContext) where T : Entity
{
    protected ApplicationDbContext DbContext { get; } = dbContext;

    public async Task<T?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await DbContext
            .Set<T>()
            .FirstOrDefaultAsync(entity => entity.Id == id, cancellationToken);
    }

    public virtual void Add(T entity)
    {
        DbContext.Add(entity);
    }
}
