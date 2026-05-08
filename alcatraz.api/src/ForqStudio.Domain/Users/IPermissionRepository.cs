namespace ForqStudio.Domain.Users;

public interface IPermissionRepository
{
    Task<Permission?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Permission>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default);

    Task<List<Permission>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Permission?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> IsInUseAsync(int id, CancellationToken cancellationToken = default);

    void Add(Permission permission);

    void Update(Permission permission);
}
