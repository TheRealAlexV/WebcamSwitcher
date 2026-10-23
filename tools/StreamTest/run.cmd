@echo off
rem Builds the MediaStream harness against the real source tree (no deployment).
setlocal
cd /d "%~dp0"

set VS=C:\Program Files\Microsoft Visual Studio\2022\Community
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1

set SRC=..\..\src\WebcamSwitcher.Source
set OUT=bin
if not exist %OUT% mkdir %OUT%

rem Local packages (WIL + C++/WinRT), same versions as the DLL build.
set PKG=..\..\packages
set INC=/I "%SRC%" /I "..\..\src\WebcamSwitcher.Shared" /I "%PKG%\Microsoft.Windows.ImplementationLibrary.1.0.260126.7\include" /I "%PKG%\Microsoft.Windows.CppWinRT.3.0.260818.1\include"

cl /nologo /std:c++20 /EHsc /O2 /W3 /DNDEBUG /DUNICODE /D_UNICODE ^
   %INC% ^
   streamtest.cpp "%SRC%\MediaStream.cpp" "%SRC%\PipeFrameClient.cpp" "%SRC%\Tools.cpp" "%SRC%\EnumNames.cpp" ^
   "%SRC%\SourceFormat.cpp" "%SRC%\WinTrace.cpp" ^
   /Fe:%OUT%\WebcamSwitcher.StreamTest.exe ^
   /Fo:%OUT%\ /link /SUBSYSTEM:CONSOLE mfplat.lib mfuuid.lib mfreadwrite.lib ole32.lib oleaut32.lib user32.lib propsys.lib advapi32.lib
exit /b %errorlevel%
