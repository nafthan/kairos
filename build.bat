@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo.
    echo The .NET SDK isn't installed yet.
    echo Install the ".NET 10 SDK" for Windows x64 from:
    echo     https://dotnet.microsoft.com/download/dotnet/10.0
    echo then run this file again.
    echo.
    pause
    exit /b 1
)

echo Building Kairos...
dotnet publish Kairos.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if errorlevel 1 (
    echo.
    echo Build failed. Copy the errors above and send them over.
    pause
    exit /b 1
)

echo.
echo Done! Your app is at:  %~dp0publish\Kairos.exe
echo You can move Kairos.exe anywhere you like.
start "" explorer "%~dp0publish"
pause
