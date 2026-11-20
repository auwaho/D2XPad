@echo off
setlocal
set "DOTNET_CLI_UI_LANGUAGE=en-US"
pushd "%~dp0" || exit /b 1
set "converter=artifacts\Desktop\D2XPad.Live.exe"
if /i "%~1"=="check" goto diagnostics
if /i "%~1"=="check-capture" goto diagnostics
if exist "%converter%" goto run
:diagnostics
set "converter=artifacts\Live\D2XPad.Live.exe"
if not defined D2XPAD_PROFILE_DIR if exist "artifacts\Desktop\D2XPad.exe" set "D2XPAD_PROFILE_DIR=%CD%\artifacts\Desktop\profiles"
if not exist "%converter%" (
  powershell -NoProfile -File "scripts\Build-Live.ps1"
  if errorlevel 1 goto done
)
:run
"%converter%" %*
:done
set "live_result=%errorlevel%"
if not "%live_result%"=="0" pause
popd
exit /b %live_result%
