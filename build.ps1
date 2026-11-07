# Build DSH Bot Manager into a single self-contained exe.
# Requires only the .NET Framework 4.x compiler that ships with Windows.
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "csc.exe not found: $csc"
}

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$out = Join-Path $dist "DshBotManager.exe"
$sources = @(Get-ChildItem (Join-Path $root "src") -Filter "*.cs" | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) {
    throw "No .cs files under src."
}

$payload = Join-Path $root "payload\astrbot_plugin_dsh_control"
$args = @(
    "/nologo",
    "/target:winexe",
    "/platform:anycpu",
    "/optimize+",
    "/utf8output",
    "/codepage:65001",
    "/out:$out",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Windows.Forms.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Web.Extensions.dll",
    "/resource:$payload\main.py,DshBotManager.Payload.main.py",
    "/resource:$payload\metadata.yaml,DshBotManager.Payload.metadata.yaml",
    "/resource:$payload\_conf_schema.json,DshBotManager.Payload._conf_schema.json"
)
$args += $sources

& $csc $args
if ($LASTEXITCODE -ne 0) {
    throw "Build failed, exit code $LASTEXITCODE"
}

Write-Host "Built: $out"
