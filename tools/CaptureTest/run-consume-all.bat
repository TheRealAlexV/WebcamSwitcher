@echo off
set OUT=C:\src\WebcamSwitcher\tools\CaptureTest\bin\Release\net9.0-windows10.0.19041.0\win-x64
set RES=C:\src\WebcamSwitcher\tools\CaptureTest\consume-result.txt
copy /y "C:\src\WebcamSwitcher\src\WebcamSwitcher.Source\x64\Release\WebcamSwitcher.Source.dll" "%OUT%\" >nul
echo === CONSUME APP-CREATED VCAMS === > "%RES%"
"%OUT%\WebcamSwitcher.CaptureTest.exe" consume "HD Pro Webcam C920 (WebcamSwitcher)" >> "%RES%" 2>&1
"%OUT%\WebcamSwitcher.CaptureTest.exe" consume "OsmoPocket3 (WebcamSwitcher)" >> "%RES%" 2>&1
"%OUT%\WebcamSwitcher.CaptureTest.exe" consume "WebcamSwitcher Virtual Camera" >> "%RES%" 2>&1
echo DONE >> "%RES%"
