param([string]$Runtime = "win-x64")

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $root 'artifacts/release'
$output = Join-Path $releaseRoot "GameTuner-$Runtime"
$archive = Join-Path $releaseRoot "GameTuner-$Runtime.zip"
$project = Join-Path $root 'src/GameTuner.App/GameTuner.App.csproj'

if ($Runtime -ne 'win-x64') {
    throw 'The bundled PresentMon executable is x64; publish with win-x64.'
}

$resolvedReleaseRoot = [IO.Path]::GetFullPath($releaseRoot)
$resolvedOutput = [IO.Path]::GetFullPath($output)
if (!$resolvedOutput.StartsWith($resolvedReleaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace output outside $resolvedReleaseRoot."
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

& dotnet restore $project --runtime $Runtime --configfile (Join-Path $root 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE." }
& dotnet publish $project -c Release -r $Runtime --self-contained true --no-restore `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $resolvedOutput
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

$required = @(
    'GameTuner.App.exe',
    'GameTuner.App.dll',
    'coreclr.dll',
    'Tools/PresentMon/PresentMon.exe',
    'Tools/PresentMon/LICENSE.txt'
)
foreach ($relative in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $resolvedOutput $relative) -PathType Leaf)) {
        throw "Release is incomplete: missing $relative"
    }
}

if (Test-Path -LiteralPath (Join-Path $resolvedOutput 'WebApp')) {
    throw 'Release contains obsolete browser assets: WebApp must be absent.'
}

$signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $resolvedOutput 'Tools/PresentMon/PresentMon.exe')
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Intel Corporation') {
    throw 'The bundled PresentMon executable is not validly signed by Intel.'
}

if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -Path (Join-Path $resolvedOutput '*') -DestinationPath $archive -CompressionLevel Optimal
Write-Host "Release folder: $resolvedOutput"
Write-Host "Share this ZIP: $archive"
