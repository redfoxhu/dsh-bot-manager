param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\DshBotManager'),
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'install.log'
function Note([string]$m) {
    try { Add-Content -LiteralPath $log -Value ((Get-Date).ToString('s') + ' ' + $m) } catch { }
}

Note "start user=$env:USERNAME"
$sourceExe = Join-Path $PSScriptRoot 'dist\DshBotManager.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) {
    Note "missing source $sourceExe"
    throw "Missing $sourceExe. Run build.ps1 first."
}

Get-Process DshBotManager -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$targetExe = Join-Path $InstallDir 'DshBotManager.exe'
Note "copy to $targetExe"
Copy-Item -LiteralPath $sourceExe -Destination $targetExe -Force
$sourceReadme = Join-Path $PSScriptRoot 'README.md'
if (Test-Path -LiteralPath $sourceReadme) {
    Copy-Item -LiteralPath $sourceReadme -Destination (Join-Path $InstallDir 'README.md') -Force
}

$icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
if (Test-Path -LiteralPath $icacls) {
    & $icacls $InstallDir /setintegritylevel '(OI)(CI)M' 2>$null | Out-Null
    & $icacls $targetExe /setintegritylevel M 2>$null | Out-Null
}

if (-not $NoShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    if ($desktop) {
        $ws = New-Object -ComObject WScript.Shell
        $lnkPath = Join-Path $desktop 'DSH Bot Manager.lnk'
        $lnk = $ws.CreateShortcut($lnkPath)
        $lnk.TargetPath = $targetExe
        $lnk.WorkingDirectory = $InstallDir
        $lnk.IconLocation = $targetExe + ',0'
        $lnk.Description = 'DSH Bot Manager'
        $lnk.Save()
    }
}

# If autostart was enabled while running the workspace copy, point it at the
# installed copy. Do not create the entry when the user never enabled it.
try {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $existing = Get-ItemProperty -Path $runKey -Name 'DshBotManager' -ErrorAction Stop
    if ($existing) {
        Set-ItemProperty -Path $runKey -Name 'DshBotManager' -Value ('"' + $targetExe + '" --autostart')
        Note "autostart target updated"
    }
} catch {
    Note "autostart entry not present"
}

Note "installed $targetExe"
Write-Host "Installed: $targetExe"
Write-Host "Start it from the desktop shortcut or Explorer."
