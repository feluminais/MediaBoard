<#
.SYNOPSIS
  Runs all tests with code coverage and writes a summary plus an HTML report to coverage-report/.
.NOTES
  --parallel none: the headless Avalonia tests are not reliable under default parallelism.
  Needs the local tools: dotnet tool restore
#>
param(
    # Release avoids the file locks a running Debug CozyCouch.exe puts on the Debug output.
    [string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $results = Join-Path $root 'TestResults'
    $report = Join-Path $root 'coverage-report'
    Remove-Item $results, $report -Recurse -Force -ErrorAction SilentlyContinue

    $env:CozyCouchNoShortcut = 'true'
    dotnet tool restore | Out-Null
    dotnet test --solution CozyCouch.sln --configuration $Configuration -- --parallel none --coverage --coverage-output-format cobertura --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed ($LASTEXITCODE)" }

    dotnet reportgenerator "-reports:$results\**\*.cobertura.xml" "-targetdir:$report" '-assemblyfilters:+CozyCouch.*;-*.Tests' '-reporttypes:TextSummary;Html'
    Get-Content (Join-Path $report 'Summary.txt')
    Write-Host "HTML report: $report\index.html"
}
finally {
    Pop-Location
}
