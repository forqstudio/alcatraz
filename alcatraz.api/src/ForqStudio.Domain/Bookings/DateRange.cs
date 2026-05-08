

namespace ForqStudio.Domain.Bookings;

public record DateRange
{
    private DateRange()
    {
    }

    public DateOnly Start { get; init; }

    public DateOnly End { get; init; }

    public int LengthInDays => End.DayNumber - Start.DayNumber;

    public static DateRange Create(DateOnly start, DateOnly end)
    {
        if (start > end)
        {
            throw new ApplicationException("start date must be before the end date");
        }

        return new DateRange
        {
            Start = start,
            End = end
        };
    }
}
