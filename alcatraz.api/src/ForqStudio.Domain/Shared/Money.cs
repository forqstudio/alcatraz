namespace ForqStudio.Domain.Shared;

public record Money(decimal Amount, Currency Currency)
{
    public static Money operator +(Money first, Money Second)
    {
        if (first.Currency != Second.Currency)
            throw new InvalidOperationException("currencies have to be equal");

        return new Money(first.Amount + Second.Amount, first.Currency);
    }

    public static Money Zero() => new Money(0, Currency.None);

    public static Money Zero(Currency currency) => new Money(0, currency);

    public bool IsZero() => this == Zero(Currency);
}
