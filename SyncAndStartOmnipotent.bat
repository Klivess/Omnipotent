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

cd serverBuild
cls
start OmnipotentProcessMonitor.exe
PAUSE
