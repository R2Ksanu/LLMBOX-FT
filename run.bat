@echo off
setlocal
title LlmodelBox Launcher
cd /d "%~dp0"
for %%F in ("dist\win-x64-standalone\LlmodelBox.exe" "app\improved\LlmodelBox.exe" "src\bin\Release\net8.0-windows\win-x64\publish\LlmodelBox.exe" "src\bin\Release\net8.0-windows\LlmodelBox.exe" "src\bin\Debug\net8.0-windows\LlmodelBox.exe" "app\LlmodelBox.exe" "LlmodelBox.exe") do (
    if exist "%%~F" (
        start "" "%%~F"
        exit /b 0
    )
)
echo No compiled application was found. Build src\LlmodelBox.csproj first.
pause
exit /b 1