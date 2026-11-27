@echo off
rem Runs MacKeys' keyboard self-test (about 15 seconds) and opens the report.
rem Don't touch the keyboard or mouse while it runs.
echo.
echo   MacKeys - autoteste do teclado
echo   Nao use o teclado nem o mouse por uns 15 segundos...
echo.
taskkill /im MacKeys.exe /f >nul 2>&1
timeout /t 1 /nobreak >nul
"%~dp0MacKeys.exe" --selftest "%TEMP%\MacKeys-autoteste.txt"
copy /y "%APPDATA%\MacKeys\debug.log" "%TEMP%\MacKeys-debug.txt" >nul 2>&1
start "" notepad "%TEMP%\MacKeys-autoteste.txt"
if exist "%LOCALAPPDATA%\Programs\MacKeys\MacKeys.exe" start "" "%LOCALAPPDATA%\Programs\MacKeys\MacKeys.exe"
