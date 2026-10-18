```bat
@echo off
title Conflux Launcher
cd /d "C:\Users\debla\Desktop\Conflux"

echo ========================================
echo          CONFLUX LAUNCHER
echo ========================================
echo.
echo Building Conflux...
echo.

dotnet build Conflux.sln -c Debug

if errorlevel 1 (
    echo.
    echo Build failed! Check the errors above.
    pause
    exit /b 1
)

echo.
echo Build successful! Launching Conflux...
echo.

start "" "Conflux.App\bin\Debug\net8.0-windows\Conflux.App.exe"

exit
```
