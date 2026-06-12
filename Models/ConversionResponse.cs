namespace UnitConversionApi.Models;

public sealed record ConversionResponse
{
    public required double OriginalValue { get; init; }
    public required string FromUnit { get; init; }
    public required double ConvertedValue { get; init; }
    public required string ToUnit { get; init; }
    public required string Category { get; init; }
    public required string Formula { get; init; }
}
