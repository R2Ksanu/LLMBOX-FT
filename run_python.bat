@echo off
cd /d "%~dp0"
if not exist "main.py" (
    echo The Python edition is not included in this workspace. Use run.bat.
    pause
    exit /b 1
)
python main.py
