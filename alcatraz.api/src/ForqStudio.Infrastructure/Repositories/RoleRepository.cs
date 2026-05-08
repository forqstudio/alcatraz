using ForqStudio.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace ForqStudio.Infrastructure.Repositories;

internal sealed class RoleRepository(ApplicationDbContext dbContext) : IRoleRepository
{
    private readonly ApplicationDbContext _dbContext = dbContext;

    public async Task<Role?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _dbContext
            .Set<Role>()
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<List<Role>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext
            .Set<Role>()
            .Include(r => r.Permissions)
            .ToListAsync(cancellationToken);
    }

    public async Task<Role?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        return await _dbContext
            .Set<Role>()
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Name == name, cancellationToken);
    }

    public async Task<bool> IsInUseAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _dbContext
            .Set<User>()
            .AnyAsync(u => u.Roles.Any(r => r.Id == id), cancellationToken);
    }

    public async Task<List<string>> GetUserIdentityIdsForRoleAsync(int roleId, CancellationToken cancellationToken = default)
    {
        return await _dbContext
            .Set<User>()
            .Where(u => u.Roles.Any(r => r.Id == roleId))
            .Select(u => u.IdentityId)
            .ToListAsync(cancellationToken);
    }

    public void Add(Role role)
    {
        foreach (var permission in role.Permissions)
        {
            _dbContext.Attach(permission);
        }

        _dbContext.Add(role);
    }

    public void Delete(Role role)
    {
        _dbContext.Remove(role);
    }
}
