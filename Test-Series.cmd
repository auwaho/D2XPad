@echo off
setlocal
set "DOTNET_CLI_UI_LANGUAGE=en-US"
pushd "%~dp0" || exit /b 1
if not exist "artifacts\SeriesProbe\D2XPad.SeriesProbe.exe" (
  powershell -NoProfile -File "scripts\Build-SeriesProbe.ps1"
  if errorlevel 1 goto done
)
"artifacts\SeriesProbe\D2XPad.SeriesProbe.exe"
:done
set "probe_result=%errorlevel%"
echo Press any key to close this window.
pause >nul
popd
exit /b %probe_result%
