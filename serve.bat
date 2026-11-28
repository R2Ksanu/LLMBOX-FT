@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -ServeOnly %*
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" (
    echo.
    echo [ERROR] serve.bat exited with code %ERR%.
    if "%~1"=="" pause
)
exit /b %ERR%
