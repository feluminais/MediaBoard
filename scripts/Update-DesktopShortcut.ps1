<#
.SYNOPSIS
    Keeps a MediaBoard shortcut on the desktop pointing at the build that was just made.

.DESCRIPTION
    Runs after every build of MediaBoard.Desktop (see the UpdateDesktopShortcut target in its project file). A rebuild
    overwrites the exe in place, so the shortcut keeps working; this only rewrites it when the exe moved (Debug to
    Release) or the icon file changed, and then refreshes the Windows icon cache so the new icon shows. It never fails
    a build.

.PARAMETER Target
    The built MediaBoard.Desktop.exe.

.PARAMETER Icon
    The .ico the exe is built with. A shortcut older than this file is rewritten.
#>
param(
    [Parameter(Mandatory)][string]$Target,
    [Parameter(Mandatory)][string]$Icon,
    [string]$Name = 'MediaBoard'
)

try {
    if (-not (Test-Path -LiteralPath $Target)) { return }

    $target = (Resolve-Path -LiteralPath $Target).Path
    $path = Join-Path ([Environment]::GetFolderPath('Desktop')) "$Name.lnk"
    $shell = New-Object -ComObject WScript.Shell

    if (Test-Path -LiteralPath $path) {
        $current = $shell.CreateShortcut($path)
        $iconChanged = (Test-Path -LiteralPath $Icon) -and ((Get-Item -LiteralPath $Icon).LastWriteTimeUtc -gt (Get-Item -LiteralPath $path).LastWriteTimeUtc)
        if ($current.TargetPath -eq $target -and -not $iconChanged) { return }
    }

    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $target
    $link.WorkingDirectory = Split-Path -Parent $target
    $link.IconLocation = "$target,0"
    $link.Description = $Name
    $link.Save()

    # Explorer keeps the old picture of a shortcut's icon until its cache is told to reload.
    & ie4uinit.exe -show 2>$null
}
catch {
    Write-Warning "MediaBoard desktop shortcut not updated: $($_.Exception.Message)"
}
exit 0
