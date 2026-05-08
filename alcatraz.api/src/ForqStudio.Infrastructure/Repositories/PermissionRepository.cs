using ForqStudio.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace ForqStudio.Infrastructure.Repositories;

internal sealed class PermissionRepository(ApplicationDbContext dbContext) : IPermissionRepository
{
    private readonly ApplicationDbContext _dbContext = dbContext;

    public async Task<Permission?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _dbContext
            .Set<Permission>()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Permission>> GetByIdsAsync(
        IEnumerable<int> ids,
        CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        return await _dbContext
            .Set<Permission>()
            .Where(p => idList.Contains(p.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Permission>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext
            .Set<Permission>()
            .ToListAsync(cancellationToken);
    }

    public async Task<Permission?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        return await _dbContext
            .Set<Permission>()
            .FirstOrDefaultAsync(p => p.Name == name, cancellationToken);
    }

    public async Task<bool> IsInUseAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _dbContext
            .Set<RolePermission>()
            .AnyAsync(rp => rp.PermissionId == id, cancellationToken);
    }

    public void Add(Permission permission)
    {
        _dbContext.Add(permission);
    }

    public void Update(Permission permission)
    {
        _dbContext.Update(permission);
    }
}
