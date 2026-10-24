namespace Jornal.Core.Results;

/// <summary>Правило суммы только для результата письма, без изменения цены заявки.</summary>
public sealed record ResultPriceSettings
{
    public const decimal DefaultMinimum = 500m;
    public bool UseMinimumPrice { get; init; } = true;
    public decimal MinimumPrice { get; init; } = DefaultMinimum;

    public ResultPriceSettings Normalize() => this with
    {
        MinimumPrice = MinimumPrice >= 0 && decimal.Truncate(MinimumPrice) == MinimumPrice
            ? MinimumPrice : DefaultMinimum
    };
}
