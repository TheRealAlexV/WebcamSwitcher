@echo off
set DIR=C:\src\WebcamSwitcher\src\WebcamSwitcher.App\bin\Release\net9.0-windows10.0.19041.0
start "" "%DIR%\WebcamSwitcher.exe"
timeout /t 9 /nobreak >nul
echo === process === > "%DIR%\launch-check.txt"
tasklist | findstr /i WebcamSwitcher >> "%DIR%\launch-check.txt" 2>&1
echo === cameras === >> "%DIR%\launch-check.txt"
dotnet run --project "C:\src\tools\CamEnum\CamEnum.csproj" -c Release >> "%DIR%\launch-check.txt" 2>&1
taskkill /f /im WebcamSwitcher.exe >nul 2>&1
echo === done === >> "%DIR%\launch-check.txt"
