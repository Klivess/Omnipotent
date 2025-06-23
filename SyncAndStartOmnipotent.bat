@echo off
taskkill /F /IM Omnipotent.exe
taskkill /F /IM OmnipotentProcessMonitor.exe

git status
git pull origin remake

REM Publish Omnipotent project
dotnet publish Omnipotent/Omnipotent.csproj --output serverBuild

REM Publish OmnipotentProcessMonitor project
mkdir serverOPMonitorBuild
dotnet publish OmnipotentProcessMonitor/OmnipotentProcessMonitor.csproj --output serverOPMonitorBuild

cd serverOPMonitorBuild
cls
start OmnipotentProcessMonitor.exe
PAUSE
