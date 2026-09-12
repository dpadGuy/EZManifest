@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "PROJECT=%ROOT%EZManifest\EZManifest.csproj"
set "OUT=%ROOT%publish"
set "ISS=%ROOT%installer.iss"
set "CLI_URL=https://github.com/dpadGuy/Steam-auto-crack/releases/download/3.5.0.7/SteamAutoCrack.CLI.zip"
set "ZIPNAME=EZManifest.zip"
set "ISCC="
set "BUILD_INSTALLER="

if not exist "%PROJECT%" (
  echo Project not found: "%PROJECT%"
  exit /b 1
)

echo.
echo  EZManifest publish
echo  ------------------
echo   1. App zip only
echo   2. App zip + installer
echo.
choice /C 12 /N /M "Choose 1 or 2: "
if errorlevel 2 (
  set "BUILD_INSTALLER=1"
) else (
  set "BUILD_INSTALLER=0"
)

echo.
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

echo.
echo Fetching SteamAutoCrack.CLI into publish\SteamAutoCrack.CLI ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\stage-steamautocrack.ps1" -PublishDir "%OUT%" -DownloadUrl "%CLI_URL%"
if errorlevel 1 (
  echo Failed to stage SteamAutoCrack.CLI.
  exit /b 1
)

echo.
echo Creating zip archive...
if exist "%OUT%\%ZIPNAME%" del /f /q "%OUT%\%ZIPNAME%"
tar.exe -a -c -f "%OUT%\%ZIPNAME%" -C "%OUT%" EZManifest.exe SteamAutoCrack.CLI
if errorlevel 1 (
  echo Zip archive failed.
  exit /b 1
)
echo Archive: "%OUT%\%ZIPNAME%"

if "%BUILD_INSTALLER%"=="0" (
  echo Published: "%OUT%\EZManifest.exe"
  start "" explorer.exe "%OUT%"
  exit /b 0
)

if exist "%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe"
if not defined ISCC if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

if not defined ISCC (
  echo Inno Setup compiler not found. Installer was skipped.
  echo Published: "%OUT%\EZManifest.exe"
  echo Archive: "%OUT%\%ZIPNAME%"
  start "" explorer.exe "%OUT%"
  exit /b 0
)

echo.
echo Building installer...
"%ISCC%" /Q "%ISS%"
if errorlevel 1 (
  echo Inno Setup compile failed.
  exit /b 1
)

echo.
echo Done: "%ROOT%installer\EZManifest-Setup-1.2.2.exe"
echo Archive: "%OUT%\%ZIPNAME%"
start "" explorer.exe "%ROOT%installer"
endlocal
