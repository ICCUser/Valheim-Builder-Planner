@echo off
rem Developer launcher: builds the C# code and runs the planner with Godot 4.7 (.NET edition).
rem Set GODOT to your Godot .NET executable, or install it with: winget install GodotEngine.GodotEngine.Mono
if "%GODOT%"=="" for /d %%D in ("%LOCALAPPDATA%\Microsoft\WinGet\Packages\GodotEngine.GodotEngine.Mono_*") do (
  for /r "%%D" %%F in (Godot_v4.7*_mono_win64.exe) do set GODOT=%%F
)
if "%GODOT%"=="" (echo Godot 4.7 .NET not found. Set the GODOT variable. & pause & exit /b 1)
cd /d "%~dp0app"
dotnet build -v q -nologo || (pause & exit /b 1)
start "" "%GODOT%" --path "%~dp0app"
