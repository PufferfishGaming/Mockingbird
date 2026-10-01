@echo off
setlocal EnableExtensions
set "INSTALLER_ROOT=C:\Mockingbird Studio Installer"
if /I "%~1"=="--local" goto install
if /I not "%~1"=="--elevated" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$p=Start-Process -FilePath '%~f0' -ArgumentList '--elevated' -Verb RunAs -Wait -PassThru; exit $p.ExitCode"
  if errorlevel 1 exit /b 1
  exit /b 0
)
:download
if not exist "%SystemRoot%\System32\curl.exe" (
  echo Windows curl.exe was not found.
  pause
  exit /b 1
)
set "BASE=https://github.com/PufferfishGaming/Mockingbird/releases/download/download/"
set "UA=Mockingbird-Studio-installer/0.1.15"
mkdir "%INSTALLER_ROOT%" >nul 2>&1
copy /Y "%~f0" "%INSTALLER_ROOT%\install-mockingbird.cmd" >nul
if errorlevel 1 goto failed
for %%F in (install-windows.ps1 Mockingbird-Studio-0.1.15-win-x64.msi SHA256SUMS.txt) do (
  echo Downloading %%F
  "%SystemRoot%\System32\curl.exe" -fL --retry 5 --retry-delay 2 --retry-all-errors --connect-timeout 30 -A "%UA%" -o "%INSTALLER_ROOT%\%%F" "%BASE%%%F"
  if errorlevel 1 goto failed
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%INSTALLER_ROOT%\install-windows.ps1"
if errorlevel 1 goto failed
exit /b 0
:install
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%INSTALLER_ROOT%\install-windows.ps1"
if errorlevel 1 goto failed
exit /b 0
:failed
echo Installation failed. The window will remain open so you can read the error.
pause
exit /b 1
