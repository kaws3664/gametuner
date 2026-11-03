param([switch]$SkipRestore)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -CommandType Application).Source
$previousArtifacts = [Environment]::GetEnvironmentVariable('GAMETUNER_UI_ARTIFACTS')

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

Push-Location -LiteralPath $root
try {
    $results = Join-Path $root 'artifacts/offline-verification'
    $env:GAMETUNER_UI_ARTIFACTS = Join-Path $results 'screenshots'
    if (!$SkipRestore) {
        Invoke-Dotnet -Arguments @('restore', 'GameTuner.sln', '--configfile', 'NuGet.Config')
    }
    Invoke-Dotnet -Arguments @('build', 'GameTuner.sln', '-c', 'Release', '--no-restore', '-v', 'minimal')
    Invoke-Dotnet -Arguments @('test', 'tests/GameTuner.Tests/GameTuner.Tests.csproj', '-c', 'Release',
        '--no-build', '--no-restore', '--filter', 'Category!=Integration',
        '--logger', 'trx;LogFileName=core-verified.trx', '--results-directory', $results,
        '--blame-hang-timeout', '60s', '-v', 'minimal')
    Invoke-Dotnet -Arguments @('test', 'tests/GameTuner.UiTests/GameTuner.UiTests.csproj', '-c', 'Release',
        '--no-build', '--no-restore', '--logger', 'trx;LogFileName=ui-verified.trx',
        '--results-directory', $results, '--blame-hang-timeout', '60s', '-v', 'minimal')

    $passed = 0
    foreach ($file in @('core-verified.trx', 'ui-verified.trx')) {
        [xml]$report = Get-Content -LiteralPath (Join-Path $results $file) -Raw
        $counters = $report.TestRun.ResultSummary.Counters
        if ([int]$counters.passed -eq 0 -or [int]$counters.failed -ne 0) {
            throw "Invalid test result in $file."
        }
        $passed += [int]$counters.passed
    }
    Write-Host "Verified: $passed passed, 0 failed. Native WPF verification and PNG: $env:GAMETUNER_UI_ARTIFACTS"
    Write-Host 'Excluded: opt-in NVIDIA/machine integrations and real gameplay. No games were launched or modified.'
}
finally {
    [Environment]::SetEnvironmentVariable('GAMETUNER_UI_ARTIFACTS', $previousArtifacts)
    Pop-Location
}
