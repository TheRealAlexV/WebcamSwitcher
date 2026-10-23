@echo off
set MODE=switcher
if exist "%~dp0mode.txt" set /p MODE=<"%~dp0mode.txt"
"C:\Dev\WebcamSwitcher\WebcamSwitcher\tools\VcamToggleTest\bin\Release\net9.0-windows10.0.19041.0\win-x64\WebcamSwitcher.VcamToggleTest.exe" %MODE% > "%~dp0vcam-result.txt" 2>&1
