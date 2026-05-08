namespace ForqStudio.Application.Abstractions.Caching;

public static class CacheKeys
{
    public const string AuthPermissionsPrefix = "auth:permissions";
    public const string AuthRolesPrefix = "auth:roles";
    public const string BookingsPrefix = "bookings";

    public static string AuthPermissions(string identityId) => $"{AuthPermissionsPrefix}-{identityId}";
    public static string AuthRoles(string identityId) => $"{AuthRolesPrefix}-{identityId}";
    public static string Booking(Guid bookingId) => $"{BookingsPrefix}-{bookingId}";
}
