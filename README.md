# Unit Conversion API

## Introduction

This application is a REST API that converts numerical values from one unit of measurement to another.

Examples:

- Meter → Feet
- Kilometer → Mile
- Kilogram → Pound
- Celsius → Fahrenheit
- Celsius → Kelvin


The application is built using:

- ASP.NET Core Web API
- .NET 10
- C#
- REST architecture


---

# What does this application do?

The user sends:

- Value to convert
- Source unit
- Target unit
- Conversion category


The application returns:

- Original value
- Converted value
- Source unit
- Target unit
- Category
- Formula used for conversion


Example:

Input:

```
100 meters
```

Output:

```
328.0839895 feet
```


---

# Prerequisites

Before running this application, install:


## 1. Install .NET 10 SDK

Download from:

https://dotnet.microsoft.com/download


After installation check:

Open Command Prompt / Terminal:

```
dotnet --version
```


Expected output:

```
10.x.x
```


---

# How to Run the Application


## Step 1

Download or clone the project.


Example folder:

```
UnitConversionApi
```


---

## Step 2

Open Command Prompt / Terminal inside the project folder.


Example:

```
cd UnitConversionApi
```


---

## Step 3

Restore required packages


Run:

```
dotnet restore
```


This downloads all required dependencies.



---

## Step 4

Build the application


Run:

```
dotnet build
```


Successful output:

```
Build succeeded
```



---

## Step 5

Start the application


Run:

```
dotnet run
```


You will see something like:

```
Now listening on:
https://localhost:7000
```


The API is now running.



---

# Open API Testing Page (Swagger)


Open browser:


```
https://localhost:<port>/swagger
OR
http://localhost:<port>/swagger/index.html
```


Example:

```
https://localhost:7000/swagger
OR
http://localhost:7000/swagger/index.html
```


Swagger allows testing the API without writing code.



---

# API Endpoint


## Convert Unit


Method:

```
POST
```


URL:

```
/api/conversion
```



---

# Request Format


Example:

```json
{
  "value":100,
  "fromUnit":"meter",
  "toUnit":"feet",
  "category":"length"
}
```


Meaning:


```
Convert 100 meters into feet
```



---

# Response Example


```json
{
  "originalValue":100,
  "fromUnit":"meter",
  "convertedValue":328.0839895,
  "toUnit":"feet",
  "category":"length",
  "formula":"100 meter × 1 / 0.3048 = 328.0839895 feet"
}
```



---

# Supported Conversion Categories


## 1. Length


Supported examples:

```
meter
feet
kilometer
mile
centimeter
```


Example:

```
100 meter
=
328.08 feet
```



---

## 2. Weight / Mass


Supported examples:

```
kilogram
gram
pound
```


Example:

```
10 kilogram
=
22.046 pound
```



---

## 3. Temperature


Supported examples:

```
celsius
fahrenheit
kelvin
```


Example:

```
100 celsius
=
212 fahrenheit
```



---

# How the Application Works


The application follows this flow:


```
User Request

      ↓

Controller

      ↓

Conversion Service

      ↓

Unit Definitions

      ↓

Response
```



---

# Project Structure


```
UnitConversionApi


Controllers

    Handles API requests


Services

    Contains conversion logic


Models

    Contains request and response objects


Data

    Contains unit information
```



---

# Conversion Logic


## Normal Units


Example:

Meter to Feet


First convert into base unit:

```
100 meter

100 × 1

= 100 meter
```


Then convert:

```
100 / 0.3048

= 328.08 feet
```



---

## Temperature


Temperature uses formulas.


Examples:


Celsius to Fahrenheit:

```
(Celsius × 9/5) + 32
```


Fahrenheit to Celsius:

```
(Fahrenheit - 32) × 5/9
```



---

# Adding New Units


Currently units are stored inside:

```
UnitDefinitions.cs
```


Example:


Adding:

```
yard
```


requires only adding:

```
1 yard = 0.9144 meter
```


No conversion code change is required.



---

# Future Improvements


The current version uses hardcoded unit definitions.


Future versions can support:


- Database storage
- Admin panel to add units
- Authentication
- Logging
- Cloud deployment
- Hundreds of conversion types



---

# Troubleshooting


## Error:

```
dotnet is not recognized
```


Solution:

Install .NET SDK and restart terminal.



---

## Error:

```
Build failed
```


Run:


```
dotnet restore
```


then:


```
dotnet build
```



---

## Error:

Application not starting


Run:


```
dotnet clean

dotnet build

dotnet run
```



---

# Summary


This project provides a simple and expandable Unit Conversion API.


Currently supports:


✓ Length  
✓ Weight / Mass  
✓ Temperature  


The architecture allows adding many more units in the future without major code changes.
