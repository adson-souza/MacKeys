# MacKeys uninstaller: closes it, removes the app, the Start menu shortcut and the autostart entry.
# Settings are kept unless the user asks to remove them too.
$target = Join-Path $env:LOCALAPPDATA 'Programs\MacKeys'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'MacKeys.lnk'
$settings = Join-Path $env:APPDATA 'MacKeys'

Write-Host ''
Write-Host '  MacKeys - desinstalação' -ForegroundColor Cyan
Write-Host ''

Get-Process MacKeys -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'MacKeys' -ErrorAction SilentlyContinue
Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:TEMP 'MacKeys-preview') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host '  App, atalho e inicialização automática removidos.'

if (Test-Path $settings) {
    $answer = Read-Host '  Apagar também suas configurações (perfis e atalhos)? [s/N]'
    if ($answer -match '^[sS]') {
        Remove-Item $settings -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host '  Configurações apagadas.'
    }
    else {
        Write-Host "  Configurações mantidas em $settings"
    }
}

Write-Host ''
Write-Host '  MacKeys desinstalado.' -ForegroundColor Green
Read-Host '  Pressione Enter para fechar'
