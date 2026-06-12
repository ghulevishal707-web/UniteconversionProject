using UnitConversionApi.Models;

namespace UnitConversionApi.Services;

public interface IConversionService
{
    ConversionResponse Convert(ConversionRequest request);
    IReadOnlyList<string> GetCategories();
    IReadOnlyList<UnitInfo> GetUnitsInCategory(string category);
    IReadOnlyList<UnitInfo> GetAllUnits();
}
