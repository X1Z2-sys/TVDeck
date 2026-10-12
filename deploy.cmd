@echo off
rem Build + register + launch TVDeck. winapp lays out AppX\ but fails to register (0x8007007B), so PowerShell registers it.
rem winapp registers its own bin\Debug\...\AppX layout, and -Register then silently keeps that stale copy: remove it first (app settings reset).
cd /d "%~dp0"
powershell -NoProfile -Command "Get-Process TVDeck -ErrorAction SilentlyContinue | Stop-Process -Force -PassThru | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue"
dotnet build TVDeck.csproj -p:Platform=x64 -nologo -v q || exit /b 1
winapp run TVDeck.csproj --arch x64 --no-build --no-launch >nul 2>&1

powershell -NoProfile -Command "Get-AppxPackage F3B552B4-5F9F-417B-87E9-3EBEA271FC86 | Remove-AppxPackage; Add-AppxPackage -Register '%~dp0bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\AppX\AppxManifest.xml' -ForceUpdateFromAnyVersion -ErrorAction Stop" || exit /b 1
start "" "shell:AppsFolder\F3B552B4-5F9F-417B-87E9-3EBEA271FC86_1z32rh13vfry6!App"
