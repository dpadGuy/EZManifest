@echo off
setlocal

set "ROOT=%~dp0"
set "PROJECT=%ROOT%EZManifest\EZManifest.csproj"
set "OUT=%ROOT%publish"

if not exist "%PROJECT%" (
  echo Project not found: "%PROJECT%"
  exit /b 1
)

echo Publishing EZManifest (Release, win-x64, single-file)...
dotnet publish "%PROJECT%" ^
  -c Release ^
  -r win-x64 ^
  -p:Platform=x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:PublishTrimmed=false ^
  -p:WindowsAppSDKSelfContained=true ^
  -p:WindowsPackageType=None ^
  -o "%OUT%"

if errorlevel 1 (
  echo Publish failed.
  exit /b 1
)

start "" explorer.exe "%OUT%"

echo.
echo Done: "%OUT%\EZManifest.exe"
endlocal
