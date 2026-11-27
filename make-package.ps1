# Builds dist\MacKeys-<version>-Instalador.exe: MacKeys itself as a self-contained single file (no .NET
# needed on the target machine). Named like this, the executable opens its installer window; once installed
# in %LOCALAPPDATA%\Programs\MacKeys it runs as the app. QuickLook is downloaded during installation.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root 'MacKeys\MacKeys.csproj'))).Project.PropertyGroup.Version | Select-Object -First 1
$build = Join-Path $root 'dist\build'
$installer = Join-Path $root "dist\MacKeys-$version-Instalador.exe"

Remove-Item $build -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $installer -Force -ErrorAction SilentlyContinue

dotnet publish (Join-Path $root 'MacKeys') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=true `
    -o $build --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou' }

Copy-Item (Join-Path $build 'MacKeys.exe') $installer
Get-Item $installer | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
