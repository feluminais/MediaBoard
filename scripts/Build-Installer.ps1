<#
.SYNOPSIS
    Builds the MediaBoard Setup.exe: publishes the self-contained app, then wraps it with Inno Setup.

.DESCRIPTION
    Runs Publish.ps1 (which refuses a working tree with uncommitted changes), then compiles scripts\Installer.iss.
    Needs Inno Setup 6 (winget install JRSoftware.InnoSetup). Both the portable zip and the installer land in the
    output folder. Nothing is signed, so Windows SmartScreen warns once on the first start of the installer.

.PARAMETER Version
    The release version, such as 0.1.0-beta. Goes into the file names and the installer.

.PARAMETER Output
    Where the files go. Defaults to artifacts\ in the repository, which git ignores.
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if (-not $Output) { $Output = 'artifacts' }
if (-not [System.IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $root $Output }
$Output = [System.IO.Path]::GetFullPath($Output)

if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.]+)?$') { throw "Version must look like 0.1.0 or 0.1.0-beta, not '$Version'." }
$versionNumber = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
    Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
    Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is not installed. Run: winget install JRSoftware.InnoSetup' }

& (Join-Path $PSScriptRoot 'Publish.ps1') -Version $Version -Output $Output
if ($LASTEXITCODE -ne 0) { throw 'Publish.ps1 failed.' }

$folder = Join-Path $Output "MediaBoard-$Version-win-x64"
& $iscc "/DAppVersion=$Version" "/DVersionNumber=$versionNumber" "/DSourceDir=$folder" "/DOutputDir=$Output" (Join-Path $PSScriptRoot 'Installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$setup = Join-Path $Output "MediaBoard-$Version-win-x64-setup.exe"
$megabytes = [math]::Round((Get-Item -LiteralPath $setup).Length / 1MB, 1)
Write-Host "Wrote $setup ($megabytes MB)"
