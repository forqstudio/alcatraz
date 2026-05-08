namespace ForqStudio.Domain.Users;

public interface IRoleRepository
{
    Task<Role?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<List<Role>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Role?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> IsInUseAsync(int id, CancellationToken cancellationToken = default);

    Task<List<string>> GetUserIdentityIdsForRoleAsync(int roleId, CancellationToken cancellationToken = default);

    void Add(Role role);

    void Delete(Role role);
}
