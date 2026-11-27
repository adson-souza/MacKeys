# Publishes MacKeys to %LOCALAPPDATA%\Programs\MacKeys (no admin needed) and starts it.
# Keeps the existing settings.json, and the "start with Windows" entry keeps pointing here.
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\MacKeys'
$settings = Join-Path $target 'settings.json'

Get-Process MacKeys -ErrorAction SilentlyContinue | Stop-Process -Force

$backup = $null
if (Test-Path $settings) { $backup = Get-Content $settings -Raw }

dotnet publish "$PSScriptRoot\MacKeys" -c Release -o $target --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou' }

if ($backup) { [IO.File]::WriteAllText($settings, $backup) }

# Launch through Explorer so MacKeys isn't a child of this script (or of whatever ran it).
explorer.exe (Join-Path $target 'MacKeys.exe')
Write-Host "MacKeys instalado em $target"
