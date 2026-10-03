@echo off
if not exist "%~dp0BmsRealtimeDemo.exe" (
  echo BmsRealtimeDemo.exe is missing. Build the project or unpack the latest release here.
  pause
  exit /b 1
)
start "" /D "%~dp0" "%~dp0BmsRealtimeDemo.exe"