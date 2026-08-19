# Builds the Windows desktop app (and the console harness) into .\publish\
#
#   .\publish.ps1                 framework-dependent  (~2 MB, needs the .NET 10 Desktop Runtime)
#   .\publish.ps1 -SelfContained  self-contained       (~160 MB, no runtime install needed)
#
# Run from this folder in PowerShell. If script execution is blocked:
#   powershell -ExecutionPolicy Bypass -File .\publish.ps1

param(
    [switch]$SelfContained,
    [string]$Runtime = 'win-x64',
    [string]$Output = 'publish'
)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

$selfContainedFlag = if ($SelfContained) { 'true' } else { 'false' }
$appOut = Join-Path $Output 'app'
$cliOut = Join-Path $Output 'cli'

Write-Host "Publishing MusicTrackGen ($Runtime, self-contained=$selfContainedFlag)" -ForegroundColor Cyan

dotnet publish src\MusicTrackGen.App\MusicTrackGen.App.csproj `
    -c Release -r $Runtime --self-contained $selfContainedFlag -o $appOut
if ($LASTEXITCODE -ne 0) { throw "Desktop app publish failed with exit code $LASTEXITCODE" }

dotnet publish src\MusicTrackGen.Cli\MusicTrackGen.Cli.csproj `
    -c Release -r $Runtime --self-contained $selfContainedFlag -o $cliOut
if ($LASTEXITCODE -ne 0) { throw "Console harness publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $appOut 'MusicTrackGen.App.exe'
Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host "  desktop app     $exe"
Write-Host "  console harness $(Join-Path $cliOut 'mtgen.exe')"

if (-not $SelfContained) {
    Write-Host ''
    Write-Host 'This is a framework-dependent build. The target machine needs the' -ForegroundColor Yellow
    Write-Host '.NET Desktop Runtime 10.x  (https://dotnet.microsoft.com/download/dotnet/10.0).' -ForegroundColor Yellow
    Write-Host 'Use -SelfContained to produce a build that carries its own runtime.' -ForegroundColor Yellow
}
