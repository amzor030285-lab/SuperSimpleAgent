@echo off
setlocal EnableExtensions
set ROOT=%~dp0
set STAGE=%ROOT%SimpleLLMChat
set OUTDIR=%ROOT%release

:: Version = the release tag, e.g. v1.0.0. Required on purpose: a release ZIP
:: silently named "dev" is a footgun, and deriving the tag inside for /f is
:: fragile -- git describe --abbrev=0 gets mangled there, and the trailing
:: backslash in %~dp0 breaks quoted git -C paths.
set VER=%~1
if not defined VER (
    echo ERROR: usage: package.bat ^<version^>   e.g. package.bat v1.0.0
    exit /b 1
)

if not exist "%STAGE%\SimpleLLMChatCLI.exe" (
    echo ERROR: %STAGE% is empty or incomplete. Run build.bat first.
    exit /b 1
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"
set ZIP=%OUTDIR%\SimpleLLMChat-%VER%.zip
if exist "%ZIP%" del /q "%ZIP%"

:: -Path on the folder keeps SimpleLLMChat as the archive's top level, so
:: extracting anywhere produces SimpleLLMChat\SimpleLLMChatGUI.exe.
powershell -NoProfile -Command ^
    "Compress-Archive -Path '%STAGE%' -DestinationPath '%ZIP%' -CompressionLevel Optimal -Force" || exit /b 1

for %%F in ("%ZIP%") do echo Created %%~nxF ^(%%~zF bytes^)
echo.
echo Upload it to the GitHub release for tag %VER%:
echo   %ZIP%
exit /b 0
