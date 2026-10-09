@echo off
rem Instalador de HKMultiplayer: el trabajo lo hace scripts\install.ps1.
rem Se puede arrastrar la carpeta del juego sobre este archivo, o pasarla: instalar.bat "C:\ruta\Hollow Knight"
chcp 65001 >nul
if not exist "%~dp0scripts\install.ps1" (
  echo No encuentro los archivos del instalador.
  echo Descomprime primero el paquete entero ^(clic derecho, Extraer todo^) y ejecuta instalar.bat desde esa carpeta.
  echo.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install.ps1" %*
set "result=%errorlevel%"
echo.
pause
exit /b %result%
