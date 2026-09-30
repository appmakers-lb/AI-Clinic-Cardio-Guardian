@echo off
setlocal
cd /d "%~dp0\.."
where dotnet >nul 2>nul || (echo .NET SDK not found. Install Visual Studio 2022 .NET desktop development workload.& exit /b 1)
dotnet restore AIClinic.CardioGuardian.sln || exit /b 1
dotnet build AIClinic.CardioGuardian.sln -c Debug || exit /b 1
echo.
echo Build succeeded.
