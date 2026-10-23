@echo off
rem Diagnostic build of the PipeFrameClient harness. Not part of the solution and
rem not shipped; it compiles the real client source against the real headers.
setlocal
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
set ROOT=%~dp0..\..
set OUT=%~dp0bin
if not exist "%OUT%" mkdir "%OUT%"
cl /nologo /std:c++20 /EHsc /W3 /O2 /MD /DNDEBUG /utf-8 /Fo:"%OUT%\\" /Fe:"%OUT%\WebcamSwitcher.PipeClientTest.exe" ^
   /I "%ROOT%\src\WebcamSwitcher.Source" ^
   /I "%ROOT%\src\WebcamSwitcher.Shared" ^
   /I "%ROOT%\packages\Microsoft.Windows.ImplementationLibrary.1.0.260126.7\include" ^
   /I "%ROOT%\packages\Microsoft.Windows.CppWinRT.3.0.260818.1\include" ^
   "%~dp0pipeclienttest.cpp" "%ROOT%\src\WebcamSwitcher.Source\PipeFrameClient.cpp" ^
   /link /SUBSYSTEM:CONSOLE WindowsApp.lib ole32.lib mfplat.lib mfuuid.lib ^
   /OUT:"%OUT%\WebcamSwitcher.PipeClientTest.exe"
