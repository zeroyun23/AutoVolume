@echo off
rem Release self-contained single-file publish (win-x64).
rem Do NOT enable PublishTrimmed: SMTC uses the WinRT projection and will lose media sessions after trimming.
rem Compression stays OFF: EnableCompressionInSingleFile=true costs about 76 MB extra private working set (measured 90.6 vs 14.0 MB).
setlocal
set ROOT=%~dp0..
set OUT=%ROOT%\artifacts\portable
set DOTNET=%USERPROFILE%\.dotnet\dotnet.exe

if not exist "%DOTNET%" (
  echo dotnet not found: %DOTNET%
  exit /b 1
)

if exist "%OUT%" rmdir /s /q "%OUT%"

"%DOTNET%" publish "%ROOT%\src\AutoVolume.App\AutoVolume.App.csproj" ^
  -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=false ^
  -p:PublishTrimmed=false ^
  -p:DebugType=None -p:DebugSymbols=false ^
  -o "%OUT%"

if errorlevel 1 (
  echo PUBLISH FAILED
  exit /b 1
)

echo.
echo Published: %OUT%
dir /b "%OUT%"
