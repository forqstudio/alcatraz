namespace ForqStudio.Domain.Shared;

public record Currency
{
    internal static readonly Currency None = new("");
    public static Currency USD = new Currency("USD");
    public static Currency EUR = new Currency("EUR");

    private Currency(string code) => Code = code;
    public string Code { get; init; }

    public static Currency FromCode(string code)
    {
        return All.FirstOrDefault(c => c.Code == code) ??
            throw new ApplicationException($"the currency code is invalid");
    }

    public static readonly IReadOnlyCollection<Currency> All = new[]
    {
        USD,
        EUR
    };
}
