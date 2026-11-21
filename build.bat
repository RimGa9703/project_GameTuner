@echo off
chcp 65001 >nul
cd /d "%~dp0"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o out
if errorlevel 1 (
    echo.
    echo [실패] .NET 8 SDK가 설치되어 있는지 확인하세요: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)
echo.
echo 완료: %~dp0out\GameTuner.exe
explorer out
pause
