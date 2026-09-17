@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\package-eithost.ps1" %*
set "packageExitCode=%ERRORLEVEL%"
if not "%packageExitCode%"=="0" echo Packaging failed. See the error above.
if "%packageExitCode%"=="0" echo Package ready. See the output paths above.
pause
exit /b %packageExitCode%
