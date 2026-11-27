# MacKeys installer: copies MacKeys.exe to the user's programs folder (no admin needed),
# adds it to the Start menu and opens it. The welcome screen offers "start with Windows".
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'MacKeys.exe'
$target = Join-Path $env:LOCALAPPDATA 'Programs\MacKeys'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'MacKeys.lnk'

Write-Host ''
Write-Host '  MacKeys 0.4.0 - instalação' -ForegroundColor Cyan
Write-Host ''

if (-not (Test-Path $source)) {
    Write-Host '  Não encontrei o MacKeys.exe ao lado deste instalador. Extraia o .zip inteiro antes.' -ForegroundColor Red
    Read-Host '  Pressione Enter para sair'
    exit 1
}

# Replacing a running copy: close it first.
Get-Process MacKeys -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item $source (Join-Path $target 'MacKeys.exe') -Force
# Files copied from a downloaded .zip carry the "from the internet" mark; this is our own app.
Unblock-File (Join-Path $target 'MacKeys.exe') -ErrorAction SilentlyContinue
Write-Host "  Instalado em $target"

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = Join-Path $target 'MacKeys.exe'
$link.WorkingDirectory = $target
$link.Description = 'Atalhos do Mac no Windows'
$link.Save()
Write-Host '  Atalho criado no menu Iniciar'

Start-Process (Join-Path $target 'MacKeys.exe')
Write-Host ''
Write-Host '  Pronto! O MacKeys está aberto (procure o ⌘ perto do relógio).' -ForegroundColor Green
Write-Host '  Na tela de boas-vindas, deixe ligado "Iniciar com o Windows".'
Write-Host ''
Read-Host '  Pressione Enter para fechar'
