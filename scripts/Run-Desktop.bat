@echo off
setlocal
cd /d "%~dp0\.."
dotnet run --project src\AIClinic.CardioGuardian.Desktop\AIClinic.CardioGuardian.Desktop.csproj
