@echo off
setlocal EnableExtensions

set "ROOT=%~dp0"
set "PROJECT=%ROOT%SalsaNOW\SalsaNOW.csproj"
set "SOLUTION=%ROOT%SalsaNOW.sln"
set "OUT=%ROOT%publish"
set "EXE=%ROOT%SalsaNOW\bin\Release\SalsaNOW.exe"
set "TOOLS_DIR=%ROOT%.tools"
set "NUGET=%TOOLS_DIR%\nuget.exe"
set "MSBUILD="

if not exist "%PROJECT%" (
  echo Project not found: "%PROJECT%"
  exit /b 1
)

if not exist "%TOOLS_DIR%" mkdir "%TOOLS_DIR%"

if not exist "%NUGET%" (
  echo Downloading nuget.exe...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile '%NUGET%'"
  if errorlevel 1 (
    echo Failed to download nuget.exe.
    exit /b 1
  )
)

echo Restoring packages...
"%NUGET%" restore "%SOLUTION%" -PackagesDirectory "%ROOT%packages" -NonInteractive
if errorlevel 1 (
  echo NuGet restore failed.
  exit /b 1
)

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
  for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
    if not defined MSBUILD set "MSBUILD=%%I"
  )
)

if not defined MSBUILD (
  echo MSBuild.exe not found. Install Visual Studio or Build Tools with the .NET desktop workload.
  exit /b 1
)

if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%"

echo Publishing SalsaNOW (Release, x64, single-file)...
"%MSBUILD%" "%PROJECT%" ^
  /t:Rebuild ^
  /p:Configuration=Release ^
  /p:Platform=AnyCPU ^
  /v:minimal

if errorlevel 1 (
  echo Publish failed.
  exit /b 1
)

if not exist "%EXE%" (
  echo Build finished but SalsaNOW.exe was not found:
  echo   %EXE%
  exit /b 1
)

copy /Y "%EXE%" "%OUT%\SalsaNOW.exe" >nul

echo.
echo Done: "%OUT%\SalsaNOW.exe"
start "" explorer.exe "%OUT%"
endlocal
