@echo off
setlocal
cd /d "%~dp0\.."
dotnet run --project tests\AIClinic.CardioGuardian.Core.Tests\AIClinic.CardioGuardian.Core.Tests.csproj
