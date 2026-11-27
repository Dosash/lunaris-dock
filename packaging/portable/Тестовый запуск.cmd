@echo off
setlocal
start "" "%~dp0LunarisDock.exe" --preview
exit /b %errorlevel%
