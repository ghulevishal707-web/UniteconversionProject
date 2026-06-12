namespace UnitConversionApi.Models;

public sealed record UnitInfo
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required string[] Aliases { get; init; }
}
