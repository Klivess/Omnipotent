taskkill /F /IM Omnipotent.exe
git status
git pull origin remake
dotnet publish --output serverBuild
cd serverBuild
@echo off
cls
Omnipotent.exe
PAUSE