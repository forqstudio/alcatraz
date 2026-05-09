using Alcatraz.Api.Controllers.Users;

namespace Alcatraz.Api.FunctionalTests.Users;

internal static class UserData
{
    public static RegisterUserRequest RegisterTestUserRequest = new("test@test.com", "test", "test", "12345");
}
