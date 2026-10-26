@echo off
setlocal EnableDelayedExpansion
set "APP_DIR=%~dp0"
set "RUNTIME_KEY=HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"

if not defined PROCESSOR_ARCHITEW6432 if /i not "%PROCESSOR_ARCHITECTURE%"=="AMD64" (
    echo This package requires 64-bit Windows.
    pause
    exit /b 1
)

reg query "%RUNTIME_KEY%" /v Installed 2>nul | findstr /i "0x1" >nul
if errorlevel 1 (
    if not exist "%APP_DIR%vc_redist.x64.exe" (
        echo The bundled Microsoft Visual C++ runtime installer is missing.
        pause
        exit /b 1
    )
    echo Installing the bundled Microsoft Visual C++ 2015-2022 x64 runtime...
    "%APP_DIR%vc_redist.x64.exe" /install /quiet /norestart
    set "INSTALL_RESULT=%ERRORLEVEL%"
    if not "!INSTALL_RESULT!"=="0" if not "!INSTALL_RESULT!"=="1638" if not "!INSTALL_RESULT!"=="3010" (
        echo Runtime installation failed with code !INSTALL_RESULT!.
        pause
        exit /b !INSTALL_RESULT!
    )
)

start "" "%APP_DIR%CH4Torque.exe"
