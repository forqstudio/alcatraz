using ForqStudio.Application.Abstractions.Clock;

namespace ForqStudio.Infrastructure.Clock;

internal sealed class DateTimeProvider : IDateTimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
}
