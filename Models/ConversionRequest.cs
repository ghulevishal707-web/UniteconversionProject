using System.ComponentModel.DataAnnotations;

namespace UnitConversionApi.Models;

public sealed record ConversionRequest
{
    [Required]
    public required double Value { get; init; }

    [Required, MinLength(1)]
    public required string FromUnit { get; init; }

    [Required, MinLength(1)]
    public required string ToUnit { get; init; }
}
