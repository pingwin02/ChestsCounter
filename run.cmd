@echo off
cd /d "%~dp0"
dotnet build -c Release --nologo -v q || (pause & exit /b 1)
"bin\Release\net10.0\ChestsCounter.exe" %*
for /f "delims=" %%f in ('dir /b /o-d chests_report_*.html 2^>nul') do (
    start "" "%%f"
    goto :done
)
:done
pause
