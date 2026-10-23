@echo off
setlocal
cd /d "%~dp0"
echo Einsatzcockpit-Druckagent installieren
 echo Bitte dieses Fenster als Administrator starten. Der Pairing-Code wird abgefragt.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set "result=%errorlevel%"
if not "%result%"=="0" echo Installation fehlgeschlagen. Bitte die Meldung oben beachten.
pause
exit /b %result%
