@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================
echo  NetSwitcher build script
echo ============================================

rem 1) Locate the .NET Framework C# compiler (csc.exe)
set "CSC="
for %%V in (v4.0.30319 v3.5 v2.0.50727) do (
    if exist "%WINDIR%\Microsoft.NET\Framework64\%%V\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\%%V\csc.exe"
    if exist "%WINDIR%\Microsoft.NET\Framework\%%V\csc.exe" if not defined CSC set "CSC=%WINDIR%\Microsoft.NET\Framework\%%V\csc.exe"
)

if not defined CSC (
    echo [ERROR] csc.exe not found. Install .NET Framework 4.x developer tools.
    echo         Typical location: %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
    pause
    exit /b 1
)
echo Using compiler: %CSC%

rem 2) Build with a response file so Windows paths and options are passed cleanly.
set "RSP=%TEMP%\netswitcher_build.rsp"
(
    echo /target:winexe
    echo /optimize+
    echo /win32icon:app.ico
    echo /win32manifest:app.manifest
    echo /out:NetSwitcher.exe
    echo NetSwitcher.cs
    echo AssemblyInfo.cs
) > "%RSP%"

"%CSC%" "@%RSP%"
set "RC=%ERRORLEVEL%"
del "%RSP%" >nul 2>&1

if not "%RC%"=="0" (
    echo [FAILED] compilation error, exit code %RC%.
    pause
    exit /b %RC%
)

echo.
echo [OK] Built NetSwitcher.exe in this folder.
echo.
pause
