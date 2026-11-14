@echo off
cd /d "%~dp0"
dotnet build -c Release --nologo -v q || (pause & exit /b 1)
"bin\Release\net10.0\ChestsCounter.exe" %*
for /f "delims=" %%f in ('dir /b /o-d reports\chests_report_*.html 2^>nul') do (
    start "" "reports\%%f"
    goto :done
)
:done
pause
