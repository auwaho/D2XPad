@echo off
setlocal
set "DOTNET_CLI_UI_LANGUAGE=en-US"
pushd "%~dp0" || exit /b 1
if exist "artifacts\85HF\D2XPad.Capture.exe" (
  "artifacts\85HF\D2XPad.Capture.exe" record %*
) else (
  dotnet run --project "src\D2XPad.Capture\D2XPad.Capture.csproj" -c Release -- record %*
)
set "capture_result=%errorlevel%"
echo.
echo Press any key to close this window.
pause >nul
popd
exit /b %capture_result%
