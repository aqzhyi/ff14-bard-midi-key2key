@echo off
rem  Build ff14-key2key.exe (console) and ff14-key2key-gui.exe (window) with the C# compiler that ships with Windows.
rem  No Visual Studio / .NET SDK needed.  Usage: double-click, or "build.bat nopause".
rem  Both exes ask for administrator rights by themselves (app.manifest / app.gui.manifest), so just double-click them.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto no_csc
if not exist "Key2Key.cs" goto no_source
if not exist "Gui.cs" goto no_source
if not exist "app.manifest" goto no_manifest
if not exist "app.gui.manifest" goto no_manifest

set "BUSY="
if exist "ff14-key2key.exe" call :check_running ff14-key2key.exe
if exist "ff14-key2key-gui.exe" call :check_running ff14-key2key-gui.exe
if defined BUSY goto in_use

echo [build] Compiler: %CSC%
echo [build] Compiling ff14-key2key.exe (console) ...
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /warn:0 /codepage:65001 /win32manifest:app.manifest /out:ff14-key2key.exe Key2Key.cs
if errorlevel 1 goto failed

echo [build] Compiling ff14-key2key-gui.exe (window) ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /warn:0 /codepage:65001 /win32manifest:app.gui.manifest /main:Ff14Key2Key.GuiProgram /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:ff14-key2key-gui.exe Key2Key.cs Gui.cs
if errorlevel 1 goto failed

echo.
echo [build] SUCCESS: ff14-key2key.exe and ff14-key2key-gui.exe were created.
echo [build] Double-click ff14-key2key-gui.exe for the window version, or ff14-key2key.exe for the console version.
echo [build] Windows will ask for administrator permission.
if /i "%~1"=="nopause" exit /b 0
echo.
pause
exit /b 0

:check_running
"%SystemRoot%\System32\tasklist.exe" /fi "imagename eq %~1" /nh 2>nul | "%SystemRoot%\System32\find.exe" /i "%~1" >nul
if not errorlevel 1 set "BUSY=%~1"
exit /b 0

:no_csc
echo.
echo [build] FAILED: csc.exe (the .NET Framework 4 C# compiler) was not found in %WINDIR%\Microsoft.NET.
echo [build] Windows 10 and 11 normally include it. Please contact whoever gave you this tool.
goto fail_exit

:no_source
echo.
echo [build] FAILED: Key2Key.cs or Gui.cs was not found next to build.bat.
echo [build] If you opened this from inside a zip file, extract the whole folder first, then try again.
goto fail_exit

:no_manifest
echo.
echo [build] FAILED: app.manifest or app.gui.manifest was not found next to build.bat.
echo [build] If you opened this from inside a zip file, extract the whole folder first, then try again.
goto fail_exit

:in_use
echo.
echo [build] FAILED: %BUSY% is still running, so Windows will not let it be replaced.
echo [build] Close every ff14-key2key window (also check the taskbar and the notification area), then try again.
goto fail_exit

:failed
set "BUSY="
if exist "ff14-key2key.exe" call :check_running ff14-key2key.exe
if exist "ff14-key2key-gui.exe" call :check_running ff14-key2key-gui.exe
if defined BUSY goto in_use
echo.
echo [build] FAILED: the compiler reported errors (see above).
echo [build] Please send a screenshot of this window to whoever gave you this tool.
goto fail_exit

:fail_exit
if /i "%~1"=="nopause" exit /b 1
echo.
pause
exit /b 1
