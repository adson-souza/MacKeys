# Builds dist\MacKeys-<version>.zip: a self-contained single-file MacKeys.exe (no .NET needed on the
# target machine) plus Instalar.cmd / Desinstalar.cmd / LEIA-ME.txt from the package folder.
$ErrorActionPreference = 'Stop'
$version = '0.4.0'
$root = $PSScriptRoot
$stage = Join-Path $root "dist\MacKeys-$version"
$zip = Join-Path $root "dist\MacKeys-$version.zip"

Remove-Item $stage, $zip -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

dotnet publish (Join-Path $root 'MacKeys') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=true `
    -o (Join-Path $root 'dist\build') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falhou' }

Copy-Item (Join-Path $root 'dist\build\MacKeys.exe') $stage
foreach ($file in Get-ChildItem (Join-Path $root 'package')) {
    if ($file.Extension -eq '.ps1') {
        # Windows PowerShell 5.1 needs a BOM to read accents and ⌘ correctly.
        $text = [IO.File]::ReadAllText($file.FullName)
        [IO.File]::WriteAllText((Join-Path $stage $file.Name), $text, (New-Object System.Text.UTF8Encoding $true))
    }
    else {
        Copy-Item $file.FullName $stage
    }
}

Compress-Archive -Path "$stage\*" -DestinationPath $zip
Get-Item $zip | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
