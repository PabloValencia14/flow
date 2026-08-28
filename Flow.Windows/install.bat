@echo off
title Instalador de Flow
echo ==========================================
echo   Instalando Flow en Windows...
echo ==========================================
powershell -ExecutionPolicy Bypass -File "%~dp0install.ps1"
pause
