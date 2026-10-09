@echo off
rem Desinstalador de HKMultiplayer: el trabajo lo hace scripts\uninstall.ps1.
rem Se puede arrastrar la carpeta del juego sobre este archivo, o pasarla: desinstalar.bat "C:\ruta\Hollow Knight"
chcp 65001 >nul
if not exist "%~dp0scripts\uninstall.ps1" (
  echo No encuentro los archivos del desinstalador.
  echo Descomprime primero el paquete entero ^(clic derecho, Extraer todo^) y ejecuta desinstalar.bat desde esa carpeta.
  echo.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\uninstall.ps1" %*
set "result=%errorlevel%"
echo.
pause
exit /b %result%
