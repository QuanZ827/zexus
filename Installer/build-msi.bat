@echo off
setlocal enabledelayedexpansion
title Zexus - Build MSI Installer (Multi-Version)

echo.
echo ==========================================
echo   Zexus - Build MSI Installer
echo   Supports Revit 2022-2026
echo ==========================================
echo.

:: Step 1: Determine project root (one level up from Installer folder)
set "PROJECT_DIR=%~dp0.."
pushd "%PROJECT_DIR%"
set "PROJECT_DIR=%CD%"
popd

echo Project: %PROJECT_DIR%
echo.

:: Step 2: Build net48 (Revit 2023/2024)
echo.
echo [1/3] Building Release - net48 (Revit 2023/2024)...
echo.
dotnet build "%PROJECT_DIR%" -f net48 --configuration Release
if errorlevel 1 (
    echo.
    echo [ERROR] net48 build failed! Fix errors above and retry.
    pause
    exit /b 1
)
echo.
echo [OK] net48 build succeeded.

:: Step 3: Build net8.0-windows (Revit 2025/2026)
echo.
echo [2/3] Building Release - net8.0-windows (Revit 2025/2026)...
echo.
dotnet build "%PROJECT_DIR%" -f net8.0-windows --configuration Release
if errorlevel 1 (
    echo.
    echo [ERROR] net8.0-windows build failed! Fix errors above and retry.
    pause
    exit /b 1
)
echo.
echo [OK] net8.0-windows build succeeded.

:: Step 3: Compile MSI with WiX v4
echo.
echo [3/3] Creating MSI installer with WiX...
echo.

set "WIX_SRC=%~dp0Zexus.wxs"

:: Create output directory
if not exist "%~dp0Output" mkdir "%~dp0Output"

:: WiX 6 resolves Source="..\..." paths in the .wxs against CWD, not against
:: the .wxs file's location. Anchor CWD to this script's directory so the
:: relative paths inside Zexus.wxs (..\bin\Release\..., ..\Zexus.addin)
:: resolve regardless of where the bat was invoked from.
pushd "%~dp0"

:: Run WiX build
wix build "%WIX_SRC%" -o "%~dp0Output\Zexus_Setup_v0.2.1.0.msi" -arch x64
if errorlevel 1 (
    echo.
    echo [ERROR] MSI compilation failed!
    pause
    exit /b 1
)

echo.
echo Verifying MSI...
if exist "%~dp0Output\Zexus_Setup_v0.2.1.0.msi" (
    echo.
    echo ==========================================
    echo   SUCCESS!
    echo.
    echo   MSI Installer: Installer\Output\Zexus_Setup_v0.2.1.0.msi
    echo.
    echo   This MSI supports Revit 2022-2026.
    echo   Supports silent install: msiexec /i Zexus_Setup.msi /qn
    echo   Users can also double-click to install.
    echo ==========================================
) else (
    echo [ERROR] MSI file not found after build!
)
echo.
pause
