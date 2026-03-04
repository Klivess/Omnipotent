@echo off
taskkill /F /IM Omnipotent.exe
taskkill /F /IM OmnipotentProcessMonitor.exe

git status
git pull origin remake

REM Publish Omnipotent project
mkdir serverBuild
dotnet publish Omnipotent/Omnipotent.csproj --output serverBuild

REM Publish OmnipotentProcessMonitor project
dotnet publish OmnipotentProcessMonitor/OmnipotentProcessMonitor.csproj --output serverBuild

REM Publish KliveLink project
dotnet publish KliveLink/KliveLink.csproj --output serverBuild -r win-x64 -c Release /p:PublishSingleFile=true /p:PublishTrimmed=true /p:CopyOutputSymbolsToPublishDirectory=false --self-contained true

cd serverBuild
cls
start OmnipotentProcessMonitor.exe
PAUSE
