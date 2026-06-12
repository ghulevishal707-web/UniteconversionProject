using UnitConversionApi.Data;
using UnitConversionApi.Models;

namespace UnitConversionApi.Services;

public sealed class ConversionService : IConversionService
{
    public ConversionResponse Convert(ConversionRequest request)
    {
        var fromResolved = UnitDefinitions.ResolveUnit(request.FromUnit)
            ?? throw new ArgumentException($"Unknown unit: '{request.FromUnit}'.");

        var toResolved = UnitDefinitions.ResolveUnit(request.ToUnit)
            ?? throw new ArgumentException($"Unknown unit: '{request.ToUnit}'.");

        if (!fromResolved.Category.Equals(toResolved.Category, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Cannot convert between different categories: " +
                $"'{request.FromUnit}' is {fromResolved.Category}, " +
                $"but '{request.ToUnit}' is {toResolved.Category}.");
        }

        var category = fromResolved.Category;
        var fromUnit = fromResolved.CanonicalName;
        var toUnit = toResolved.CanonicalName;

        double result;
        string formula;

        if (category.Equals("temperature", StringComparison.OrdinalIgnoreCase))
        {
            (result, formula) = ConvertTemperature(request.Value, fromUnit, toUnit);
        }
        else
        {
            (result, formula) = ConvertLinear(request.Value, fromUnit, toUnit, category);
        }

        return new ConversionResponse
        {
            OriginalValue = request.Value,
            FromUnit = fromUnit,
            ConvertedValue = Math.Round(result, 10),
            ToUnit = toUnit,
            Category = category,
            Formula = formula
        };
    }

    private static (double Result, string Formula) ConvertLinear(
        double value, string fromUnit, string toUnit, string category)
    {
        var units = UnitDefinitions.Categories[category];
        var fromFactor = units[fromUnit].ToBaseFactor;
        var toFactor = units[toUnit].ToBaseFactor;

        var baseValue = value * fromFactor;
        var result = baseValue / toFactor;

        var formula = $"{value} {fromUnit} × {fromFactor} / {toFactor} = {Math.Round(result, 10)} {toUnit}";
        return (result, formula);
    }

    private static (double Result, string Formula) ConvertTemperature(
        double value, string fromUnit, string toUnit)
    {
        if (fromUnit == toUnit)
            return (value, $"{value} {fromUnit} = {value} {toUnit}");

        double celsius = fromUnit switch
        {
            "celsius"    => value,
            "fahrenheit" => (value - 32.0) * 5.0 / 9.0,
            "kelvin"     => value - 273.15,
            _ => throw new ArgumentException($"Unknown temperature unit: '{fromUnit}'.")
        };

        double result = toUnit switch
        {
            "celsius"    => celsius,
            "fahrenheit" => celsius * 9.0 / 5.0 + 32.0,
            "kelvin"     => celsius + 273.15,
            _ => throw new ArgumentException($"Unknown temperature unit: '{toUnit}'.")
        };

        var formula = (fromUnit, toUnit) switch
        {
            ("celsius", "fahrenheit")    => $"({value} × 9/5) + 32 = {Math.Round(result, 10)}",
            ("fahrenheit", "celsius")    => $"({value} - 32) × 5/9 = {Math.Round(result, 10)}",
            ("celsius", "kelvin")        => $"{value} + 273.15 = {Math.Round(result, 10)}",
            ("kelvin", "celsius")        => $"{value} - 273.15 = {Math.Round(result, 10)}",
            ("fahrenheit", "kelvin")     => $"({value} - 32) × 5/9 + 273.15 = {Math.Round(result, 10)}",
            ("kelvin", "fahrenheit")     => $"({value} - 273.15) × 9/5 + 32 = {Math.Round(result, 10)}",
            _ => $"{value} {fromUnit} = {Math.Round(result, 10)} {toUnit}"
        };

        return (Math.Round(result, 10), formula);
    }

    public IReadOnlyList<string> GetCategories() =>
        UnitDefinitions.Categories.Keys.ToList();

    public IReadOnlyList<UnitInfo> GetUnitsInCategory(string category)
    {
        var normalized = category.Trim();

        if (!UnitDefinitions.Categories.TryGetValue(normalized, out var units))
            throw new ArgumentException($"Unknown category: '{category}'.");

        return units.Select(kvp => new UnitInfo
        {
            Name = kvp.Key,
            Category = normalized.ToLowerInvariant(),
            Aliases = kvp.Value.Aliases
        }).ToList();
    }

    public IReadOnlyList<UnitInfo> GetAllUnits() =>
        UnitDefinitions.Categories
            .SelectMany(cat => cat.Value.Select(unit => new UnitInfo
            {
                Name = unit.Key,
                Category = cat.Key,
                Aliases = unit.Value.Aliases
            }))
            .ToList();
}
