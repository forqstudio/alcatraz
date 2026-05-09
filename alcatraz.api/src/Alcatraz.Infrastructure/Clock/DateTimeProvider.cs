using Alcatraz.Application.Abstractions.Clock;

namespace Alcatraz.Infrastructure.Clock;

internal sealed class DateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}
