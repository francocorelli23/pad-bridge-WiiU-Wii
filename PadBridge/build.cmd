@echo off
rem Builds PadBridge.exe with the Roslyn compiler from Visual Studio Build Tools (targets .NET Framework 4.8).
setlocal
set CSC=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe
if not exist "%CSC%" (
  for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -find MSBuild\**\Roslyn\csc.exe`) do set CSC=%%i
)
if not exist "%CSC%" ( echo csc.exe not found & exit /b 1 )

cd /d "%~dp0"
if not exist bin mkdir bin
"%CSC%" -nologo -target:winexe -platform:x64 -optimize+ -langversion:latest -nullable:disable ^
  -out:bin\PadBridge.exe -win32manifest:app.manifest ^
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll ^
  -recurse:src\*.cs
if errorlevel 1 exit /b 1
echo Built bin\PadBridge.exe
