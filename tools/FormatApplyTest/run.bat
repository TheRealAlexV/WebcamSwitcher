@echo off
set OUT=C:\src\WebcamSwitcher\tools\FormatApplyTest\bin\Release\net9.0-windows10.0.19041.0\win-x64
copy /y "C:\src\WebcamSwitcher\src\WebcamSwitcher.Source\x64\Release\WebcamSwitcher.Source.dll" "%OUT%\" >nul
"%OUT%\WebcamSwitcher.FormatApplyTest.exe" > "C:\src\WebcamSwitcher\tools\FormatApplyTest\result.txt" 2>&1
echo DONE >> "C:\src\WebcamSwitcher\tools\FormatApplyTest\result.txt"
