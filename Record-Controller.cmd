@echo off
setlocal
set "DOTNET_CLI_UI_LANGUAGE=en-US"
pushd "%~dp0" || exit /b 1
dotnet run --project "src\D2XPad.Capture\D2XPad.Capture.csproj" -c Release -- %*
set "capture_result=%errorlevel%"
if "%~1"=="" (
  echo.
  echo Press any key to close this window.
  pause >nul
)
popd
exit /b %capture_result%
