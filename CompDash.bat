@echo off
rem Launcher for CompDash. Passing files is optional:
rem     CompDash.bat "C:\path\clip.mp4" "C:\path\photo.png"
start "" "%~dp0dist\CompDash.exe" %*
