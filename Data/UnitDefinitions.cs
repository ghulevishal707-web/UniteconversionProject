using System.Collections.ObjectModel;

namespace UnitConversionApi.Data;

public static class UnitDefinitions
{
    public static readonly Dictionary<string, Dictionary<string, UnitDefinition>> Categories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["length"] = new()
            {
                ["meter"] = new()
                {
                    ToBaseFactor = 1,
                    Aliases = ["m", "meter", "metre"]
                },

                ["kilometer"] = new()
                {
                    ToBaseFactor = 1000,
                    Aliases = ["km", "kilometer"]
                },

                ["centimeter"] = new()
                {
                    ToBaseFactor = 0.01,
                    Aliases = ["cm", "centimeter"]
                },

                ["millimeter"] = new()
                {
                    ToBaseFactor = 0.001,
                    Aliases = ["mm"]
                },

                ["feet"] = new()
                {
                    ToBaseFactor = 0.3048,
                    Aliases = ["ft", "foot", "feet"]
                },

                ["inch"] = new()
                {
                    ToBaseFactor = 0.0254,
                    Aliases = ["in", "inch"]
                },

                ["mile"] = new()
                {
                    ToBaseFactor = 1609.34,
                    Aliases = ["mi", "mile"]
                }
            },


            ["weight"] = new()
            {
                ["kilogram"] = new()
                {
                    ToBaseFactor = 1,
                    Aliases = ["kg", "kgs"]
                },

                ["gram"] = new()
                {
                    ToBaseFactor = 0.001,
                    Aliases = ["g", "gm", "gram"]
                },

                ["milligram"] = new()
                {
                    ToBaseFactor = 0.000001,
                    Aliases = ["mg"]
                },

                ["pound"] = new()
                {
                    ToBaseFactor = 0.453592,
                    Aliases = ["lb", "lbs"]
                },

                ["ounce"] = new()
                {
                    ToBaseFactor = 0.0283495,
                    Aliases = ["oz"]
                },

                ["ton"] = new()
                {
                    ToBaseFactor = 1000,
                    Aliases = ["t"]
                }
            },


            ["size"] = new()
            {
                ["byte"] = new()
                {
                    ToBaseFactor = 1,
                    Aliases = ["b"]
                },

                ["kilobyte"] = new()
                {
                    ToBaseFactor = 1024,
                    Aliases = ["kb"]
                },

                ["megabyte"] = new()
                {
                    ToBaseFactor = 1024 * 1024,
                    Aliases = ["mb"]
                },

                ["gigabyte"] = new()
                {
                    ToBaseFactor = 1024 * 1024 * 1024,
                    Aliases = ["gb"]
                },

                ["terabyte"] = new()
                {
                    ToBaseFactor = 1024L * 1024 * 1024 * 1024,
                    Aliases = ["tb"]
                }
            },


            ["temperature"] = new()
            {
                ["celsius"] = new()
                {
                    Aliases = ["c", "°c"]
                },

                ["fahrenheit"] = new()
                {
                    Aliases = ["f", "°f"]
                },

                ["kelvin"] = new()
                {
                    Aliases = ["k"]
                }
            }
        };


    private static readonly Dictionary<string, (string CanonicalName, string Category)> AliasIndex =
        Categories
        .SelectMany(category =>
            category.Value.SelectMany(unit =>
                unit.Value.Aliases.Select(alias =>
                    new
                    {
                        Alias = alias,
                        Unit = unit.Key,
                        Category = category.Key
                    })))
        .ToDictionary(
            x => x.Alias,
            x => (x.Unit, x.Category),
            StringComparer.OrdinalIgnoreCase
        );


    public static (string CanonicalName, string Category)? ResolveUnit(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var key = input.Trim();

        if (AliasIndex.TryGetValue(key, out var result))
            return result;

        return null;
    }
}


public sealed class UnitDefinition
{
    public double ToBaseFactor { get; set; }

    public string[] Aliases { get; set; } = [];
}