@echo off
REM Lanza el script que agrega el retardo al tray (se autoeleva y pide UAC).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tray-AgregarRetardo.ps1"
