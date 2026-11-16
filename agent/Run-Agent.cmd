@echo off
rem Mtaalam V2V sizing agent - measures this VM for 5 minutes (pass e.g. -Minutes 15 for longer).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0MtaalamV2V-Agent.ps1" %*
pause
