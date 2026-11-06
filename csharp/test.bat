@echo off
rem  Build and run the unit tests (no test framework needed, only the C# compiler that ships with Windows).
rem  Usage: double-click, or "test.bat nopause". Exit code = 0 when every test passed.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto no_csc
if not exist "Key2Key.cs" goto no_source
if not exist "Tests.cs" goto no_source

echo [test] Compiler: %CSC%
echo [test] Compiling ff14-key2key-tests.exe ...
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /warn:0 /codepage:65001 /main:Ff14Key2Key.TestProgram /out:ff14-key2key-tests.exe Key2Key.cs Tests.cs
if errorlevel 1 goto failed

echo [test] Running ...
.\ff14-key2key-tests.exe
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" goto tests_failed

echo.
echo [test] SUCCESS: all tests passed.
if /i "%~1"=="nopause" exit /b 0
echo.
pause
exit /b 0

:no_csc
echo.
echo [test] FAILED: csc.exe (the .NET Framework 4 C# compiler) was not found in %WINDIR%\Microsoft.NET.
set "RC=1"
goto fail_exit

:no_source
echo.
echo [test] FAILED: Key2Key.cs or Tests.cs was not found next to test.bat.
set "RC=1"
goto fail_exit

:failed
echo.
echo [test] FAILED: the compiler reported errors (see above).
set "RC=1"
goto fail_exit

:tests_failed
echo.
echo [test] FAILED: some tests did not pass (exit code %RC%).
goto fail_exit

:fail_exit
if /i "%~1"=="nopause" exit /b %RC%
echo.
pause
exit /b %RC%
