# Unit Conversion API

RESTful ASP.NET Core Web API for converting values between units of measurement.
Built with **.NET 10** (LTS), native OpenAPI 3.1, and Scalar interactive docs.

## Categories

| Category    | Example units                                   |
|-------------|------------------------------------------------ |
| Length      | meter, kilometer, mile, foot, inch, yard        |
| Temperature | Celsius, Fahrenheit, Kelvin                     |
| Mass        | kilogram, gram, pound, ounce, stone             |
| Volume      | liter, gallon, quart, pint, cup, fluid ounce    |
| Area        | square meter, acre, hectare, square foot         |
| Speed       | m/s, km/h, mph, knots                           |
| Time        | second, minute, hour, day, week                 |
| Data        | byte, kilobyte, megabyte, gigabyte, terabyte    |

Every unit accepts its canonical name **or** any alias (`kg`, `lbs`, `°C`, `ft`).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```bash
dotnet --version   # should print 10.0.x
```

## Run Locally

```bash
cd UnitConversionApi
dotnet restore
dotnet run
```

The API starts at:
- **http://localhost:5000**
- **https://localhost:5001**

Interactive docs: **[localhost](https://localhost:5001/scalar/v1**)

## Endpoints

### Convert (POST)

```bash
curl -X POST [localhost](https://localhost:5001/api/conversion/convert) \
  -H "Content-Type: application/json" \
  -d '{"value": 100, "fromUnit": "meter", "toUnit": "foot"}'
```

### Convert (GET)

```bash
curl "[localhost](https://localhost:5001/api/conversion/convert?value=37&from=celsius&to=fahrenheit)"
```

### List categories

```bash
curl [localhost](https://localhost:5001/api/conversion/categories)
```

### List units in a category

```bash
curl [localhost](https://localhost:5001/api/conversion/categories/mass/units)
```

### List all units

```bash
curl [localhost](https://localhost:5001/api/conversion/units)
```

## Extending

To add units, edit `Data/UnitDefinitions.cs` — add a line with the base unit,
factor, and aliases. The alias index rebuilds automatically at startup.

For non-linear conversions (like temperature), add a dedicated method in
`ConversionService`.

To scale to hundreds of units, swap `UnitDefinitions` for a database-backed
`IUnitRepository` — the service and controller layers stay unchanged.
