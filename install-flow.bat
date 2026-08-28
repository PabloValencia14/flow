@echo off
setlocal
call "%~dp0installer\install-flow.bat" %*
exit /b %ERRORLEVEL%
