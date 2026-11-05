<#
.SYNOPSIS
    Builds the self-contained win-x64 folder of MediaBoard and zips it, so a PC with no .NET can run it.

.DESCRIPTION
    Refuses to run when the working tree has uncommitted changes, so a zip always matches a commit. The folder carries
    .NET and ASP.NET Core (the desktop app hosts the sharing server). It holds no debug symbols and none of your data:
    a published app keeps its library, settings and certificate under %LOCALAPPDATA%\MediaBoard, never beside the exe.
    Nothing is signed, so Windows SmartScreen warns once on the first start.

.PARAMETER Version
    The version in the file name. Defaults to the date and the short commit, such as 2026.11.05-23074c7.

.PARAMETER Output
    Where the folder and the zip go. Defaults to artifacts\ in the repository, which git ignores.
    A relative path is taken from the repository root, not from where you run the script.
#>
param(
    [string]$Version,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Push-Location -LiteralPath $root
try {

$dirty = git status --porcelain --untracked-files=all
if ($LASTEXITCODE -ne 0) { throw 'git status failed.' }
if ($dirty) { throw "The working tree has uncommitted changes. Commit or stash them first:`n$($dirty -join "`n")" }

if (-not $Version) {
    $sha = (git rev-parse --short HEAD).Trim()
    $Version = "$(Get-Date -Format 'yyyy.MM.dd')-$sha"
}
if (-not $Output) { $Output = 'artifacts' }
# A full path, from the repository root: the checks below cut the folder's path off each file's.
if (-not [System.IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $root $Output }
$Output = [System.IO.Path]::GetFullPath($Output)

$name = "MediaBoard-$Version-win-x64"
$folder = Join-Path $Output $name
$zip = Join-Path $Output "$name.zip"
New-Item -ItemType Directory -Force -Path $Output | Out-Null
if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

# MediaBoardNoShortcut: a publish builds first, and a build would move the desktop shortcut to this folder.
dotnet publish src/MediaBoard.Desktop -c Release -r win-x64 --self-contained true -o $folder `
    -p:MediaBoardNoShortcut=true -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# The folder must hold the runtimes, and nothing a user or this repository's tooling left behind.
foreach ($needed in 'MediaBoard.Desktop.exe', 'hostfxr.dll', 'coreclr.dll', 'Microsoft.AspNetCore.dll', 'Microsoft.AspNetCore.Server.Kestrel.Core.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $folder $needed))) { throw "The published folder has no $needed." }
}
# A package can bring its own symbols (libHarfBuzzSharp.pdb); the app ships none.
Get-ChildItem -LiteralPath $folder -Recurse -Force -Filter *.pdb | Remove-Item -Force
$unwanted = Get-ChildItem -LiteralPath $folder -Recurse -Force | ForEach-Object {
    $relative = $_.FullName.Substring($folder.Length).TrimStart([char]92)
    if ($_.Extension -in '.db', '.sqlite', '.pfx', '.bin' -or $_.Name -in 'settings.json', 'pending-progress.json' -or
        $relative -match '(^|[\\/])(\.claude|\.superpowers|docs)([\\/]|$)') { $relative }
}
if ($unwanted) { throw "The published folder holds files that should not ship:`n$($unwanted -join "`n")" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($folder, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$megabytes = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
Write-Host "Wrote $zip ($megabytes MB)"

}
finally {
    Pop-Location
}
