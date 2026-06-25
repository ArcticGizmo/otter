@echo off
setlocal

:: Read version from csproj if not passed as argument
if not "%~1"=="" (
    set VERSION=%~1
) else (
    for /f "tokens=*" %%i in ('powershell -NoProfile -Command "(Select-Xml -Path src\Otter.csproj -XPath \"//Version\").Node.InnerText"') do set VERSION=%%i
)

if "%VERSION%"=="" (
    echo Error: Could not determine version. Pass as argument: publish.bat 1.2.3
    exit /b 1
)

echo Building Otter v%VERSION%...

dotnet publish src\Otter.csproj -c Release -r win-x64 --self-contained true ^
    -p:DebugType=embedded ^
    -p:Version=%VERSION% ^
    -o publish\

if %ERRORLEVEL% neq 0 (
    echo Build failed.
    exit /b %ERRORLEVEL%
)

echo Packaging ...

dnx vpk pack --packId Otter --packTitle "Otter" --packVersion %VERSION% --packDir publish\ --mainExe Otter.exe --outputDir releases\

if %ERRORLEVEL% neq 0 (
    echo Pack failed. Is the vpk CLI installed? Run: dotnet tool install -g vpk
    exit /b %ERRORLEVEL%
)

echo.
echo Release artifacts ready in: releases\
echo Upload to: https://github.com/ArcticGizmo/otter/releases/new?tag=v%VERSION%
