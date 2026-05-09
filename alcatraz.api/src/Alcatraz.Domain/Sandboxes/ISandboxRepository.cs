namespace Alcatraz.Domain.Sandboxes;

public interface ISandboxRepository
{
    Task<Sandbox?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Add(Sandbox sandbox);
}
