# Clean reinstall of MacKeys: stops it, removes the app, its settings, preview cache and autostart entry,
# then publishes a fresh copy and starts it (the welcome screen appears again).
# Run it through clean-install.cmd (double-click), so it runs as a normal Windows program.
$target = Join-Path $env:LOCALAPPDATA 'Programs\MacKeys'
$log = Join-Path $PSScriptRoot 'clean-install.log'
Remove-Item $log -ErrorAction SilentlyContinue
function Log($message) { Add-Content -Path $log -Value "$(Get-Date -Format HH:mm:ss) $message" -Encoding utf8 }

Log 'Parando o MacKeys'
Get-Process MacKeys -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

Log 'Removendo inicialização com o Windows'
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'MacKeys' -ErrorAction SilentlyContinue

foreach ($folder in @($target, (Join-Path $env:APPDATA 'MacKeys'), (Join-Path $env:TEMP 'MacKeys-preview'))) {
    if (Test-Path $folder) {
        Remove-Item $folder -Recurse -Force -ErrorAction SilentlyContinue
        Log "Removido: $folder (ainda existe: $(Test-Path $folder))"
    }
}

Log 'Publicando versão nova'
$output = & dotnet publish (Join-Path $PSScriptRoot 'MacKeys') -c Release -o $target --nologo -v q 2>&1
if ($LASTEXITCODE -ne 0) { Log "ERRO no publish: $output"; exit 1 }

Log 'Iniciando o MacKeys'
Start-Process (Join-Path $target 'MacKeys.exe')
Start-Sleep -Seconds 3
Log "Rodando: $([bool](Get-Process MacKeys -ErrorAction SilentlyContinue))"
Log 'Concluído'
