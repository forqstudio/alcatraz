using ForqStudio.Domain.Abstractions;
using ForqStudio.Domain.Users.Events;

namespace ForqStudio.Domain.Users;

public sealed class User : Entity
{
    private User(
        Guid id,
        FirstName firstName,
        LastName lastName,
        Email email) 
        : base(id)
    {
        FirstName = firstName;
        LastName = lastName;
        Email = email;
    }

    public FirstName FirstName { get; private set; }

    public LastName LastName { get; private set; }

    public Email Email { get; private set; }

    public string IdentityId { get; private set; } = string.Empty;

    private readonly List<Role> _roles = new();

    public IReadOnlyCollection<Role> Roles => _roles.ToList();

    public static User Create(FirstName firstName, LastName lastName, Email email)
    {
        var user = new User(Guid.NewGuid(), firstName, lastName, email);
        user._roles.Add(Role.User);
        
        user.RaiseDomainEvent(new UserCreatedDomainEvent(user.Id));

        return user;
    }

    public void SetIdentityId(string identityId)
    {
        IdentityId = identityId;
    }
}
