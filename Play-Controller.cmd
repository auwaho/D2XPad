@echo off
setlocal
set "DOTNET_CLI_UI_LANGUAGE=en-US"
pushd "%~dp0" || exit /b 1
set "converter=artifacts\Live\D2XPad.Live.exe"
if not defined D2XPAD_PROFILE_DIR if exist "artifacts\Desktop\D2XPad.exe" set "D2XPAD_PROFILE_DIR=%CD%\artifacts\Desktop\profiles"
if not exist "%converter%" (
  powershell -NoProfile -File "scripts\Build-Live.ps1"
  if errorlevel 1 goto done
)
"%converter%" %*
:done
set "live_result=%errorlevel%"
if "%~1"=="" if not "%live_result%"=="0" pause
popd
exit /b %live_result%
