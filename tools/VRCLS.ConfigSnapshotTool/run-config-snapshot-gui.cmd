@echo off
setlocal
cd /d "%~dp0"

where py >nul 2>nul
if %ERRORLEVEL% EQU 0 (
    py -3 "%~dp0config_snapshot_gui.py"
) else (
    python "%~dp0config_snapshot_gui.py"
)

if errorlevel 1 pause
