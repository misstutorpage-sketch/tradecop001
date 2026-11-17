param(
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$appProject = Join-Path $root 'src\App\tradecop001\tradecop001.csproj'
$toolProject = Join-Path $root 'src\App\tradecop001.LicenseTool\tradecop001.LicenseTool.csproj'
$publishDir = Join-Path $root "publish\$Runtime"

$publicKeyFile = Join-Path $root 'src\TradeCop001.Licensing\LicensingPublicKey.cs'
if ((Get-Content -Raw $publicKeyFile) -match 'Base64\s*=\s*"REPLACE_WITH_YOUR_PUBLIC_KEY_BASE64"') {
    throw 'Set your public key in LicensingPublicKey.cs before publishing.'
}

Write-Host "Publishing tradecop001 ($Runtime, $Configuration)..."
dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }

Write-Host 'Publishing LicenseTool...'
dotnet publish $toolProject -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:DebugType=None `
    -o (Join-Path $root 'publish\LicenseTool')

if ($LASTEXITCODE -ne 0) { throw 'LicenseTool publish failed.' }

Copy-Item (Join-Path $root 'docs\DISTRIBUTION.md') (Join-Path $publishDir 'README.txt') -Force

Write-Host ""
Write-Host "Share the contents of: $publishDir"
Write-Host "Keep tradecop001-private-key.txt OUT of anything you share."
