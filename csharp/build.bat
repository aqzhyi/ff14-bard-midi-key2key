@echo off
rem  Build ff14-key2key.exe from Key2Key.cs with the C# compiler that ships with Windows.
rem  No Visual Studio / .NET SDK needed.  Usage: double-click, or "build.bat nopause".
rem  The finished exe asks for administrator rights by itself (app.manifest), so just double-click it.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto no_csc
if not exist "Key2Key.cs" goto no_source
if not exist "app.manifest" goto no_manifest

if not exist "ff14-key2key.exe" goto compile
"%SystemRoot%\System32\tasklist.exe" /fi "imagename eq ff14-key2key.exe" /nh 2>nul | "%SystemRoot%\System32\find.exe" /i "ff14-key2key.exe" >nul
if not errorlevel 1 goto in_use

:compile
echo [build] Compiler: %CSC%
echo [build] Compiling Key2Key.cs ...
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /warn:0 /codepage:65001 /win32manifest:app.manifest /out:ff14-key2key.exe Key2Key.cs
if errorlevel 1 goto failed

echo.
echo [build] SUCCESS: ff14-key2key.exe was created.
echo [build] Double-click ff14-key2key.exe to start it (Windows will ask for administrator permission).
if /i "%~1"=="nopause" exit /b 0
echo.
pause
exit /b 0

:no_csc
echo.
echo [build] FAILED: csc.exe (the .NET Framework 4 C# compiler) was not found in %WINDIR%\Microsoft.NET.
echo [build] Windows 10 and 11 normally include it. Please contact whoever gave you this tool.
goto fail_exit

:no_source
echo.
echo [build] FAILED: Key2Key.cs was not found next to build.bat.
echo [build] If you opened this from inside a zip file, extract the whole folder first, then try again.
goto fail_exit

:no_manifest
echo.
echo [build] FAILED: app.manifest was not found next to build.bat.
echo [build] If you opened this from inside a zip file, extract the whole folder first, then try again.
goto fail_exit

:in_use
echo.
echo [build] FAILED: ff14-key2key.exe is still running, so Windows will not let it be replaced.
echo [build] Close every ff14-key2key window (also check the taskbar), then try again.
goto fail_exit

:failed
"%SystemRoot%\System32\tasklist.exe" /fi "imagename eq ff14-key2key.exe" /nh 2>nul | "%SystemRoot%\System32\find.exe" /i "ff14-key2key.exe" >nul
if not errorlevel 1 goto in_use
echo.
echo [build] FAILED: the compiler reported errors (see above).
echo [build] Please send a screenshot of this window to whoever gave you this tool.
goto fail_exit

:fail_exit
if /i "%~1"=="nopause" exit /b 1
echo.
pause
exit /b 1
