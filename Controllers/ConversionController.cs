using Microsoft.AspNetCore.Mvc;
using UnitConversionApi.Models;
using UnitConversionApi.Services;

namespace UnitConversionApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class ConversionController : ControllerBase
{
    private readonly IConversionService _conversionService;

    public ConversionController(IConversionService conversionService)
    {
        _conversionService = conversionService;
    }

    /// <summary>
    /// Convert a value from one unit to another (JSON body).
    /// </summary>
    [HttpPost("convert")]
    [ProducesResponseType(typeof(ConversionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<ConversionResponse> Convert([FromBody] ConversionRequest request)
    {
        try
        {
            var result = _conversionService.Convert(request);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Problem(
                detail: ex.Message,
                title: "Invalid conversion request",
                statusCode: StatusCodes.Status400BadRequest
            );
        }
    }

    /// <summary>
    /// Convert a value from one unit to another (query parameters).
    /// </summary>
    [HttpGet("convert")]
    [ProducesResponseType(typeof(ConversionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<ConversionResponse> ConvertGet(
        [FromQuery(Name = "value")] double value,
        [FromQuery(Name = "from")] string from,
        [FromQuery(Name = "to")] string to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            return Problem(
                detail: "Both 'from' and 'to' query parameters are required.",
                title: "Missing parameters",
                statusCode: StatusCodes.Status400BadRequest
            );
        }

        var request = new ConversionRequest
        {
            Value = value,
            FromUnit = from,
            ToUnit = to
        };

        return Convert(request);
    }

    /// <summary>
    /// List all supported conversion categories.
    /// </summary>
    [HttpGet("categories")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> GetCategories()
    {
        return Ok(_conversionService.GetCategories());
    }

    /// <summary>
    /// List all units in a specific category.
    /// </summary>
    [HttpGet("categories/{category}/units")]
    [ProducesResponseType(typeof(IReadOnlyList<UnitInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<UnitInfo>> GetUnitsInCategory(string category)
    {
        try
        {
            return Ok(_conversionService.GetUnitsInCategory(category));
        }
        catch (ArgumentException)
        {
            return Problem(
                detail: $"Unknown category: '{category}'.",
                title: "Category not found",
                statusCode: StatusCodes.Status404NotFound
            );
        }
    }

    /// <summary>
    /// List every supported unit across all categories.
    /// </summary>
    [HttpGet("units")]
    [ProducesResponseType(typeof(IReadOnlyList<UnitInfo>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<UnitInfo>> GetAllUnits()
    {
        return Ok(_conversionService.GetAllUnits());
    }
}
