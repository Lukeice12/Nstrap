param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
Push-Location $root
try {
    & $dotnet build Nstrap.slnx -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $dotnet run --project tests/Nstrap.Checks/Nstrap.Checks.csproj -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
} finally { Pop-Location }
