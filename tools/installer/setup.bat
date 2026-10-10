@echo off
rem Instalador de HKMultiplayer: el trabajo lo hace scripts\setup.ps1.
rem Doble clic: ventana para instalar, actualizar o desinstalar (en el idioma de Windows). Se puede arrastrar la
rem carpeta del juego sobre este archivo. En la consola: "HKMultiplayer Setup.bat" /install|/uninstall "C:\ruta\Hollow Knight"
chcp 65001 >nul
if not exist "%~dp0scripts\setup.ps1" (
  echo No encuentro los archivos del instalador. Descomprime primero el paquete entero ^(clic derecho, Extraer todo^).
  echo The installer files are missing. Extract the whole package first ^(right-click, Extract All^).
  echo.
  pause
  exit /b 1
)
if /i "%~1"=="/install" goto console
if /i "%~1"=="/uninstall" goto console
start "" /min powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\setup.ps1" -GameDir "%~1"
exit /b 0

:console
set "mode=%~1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\setup.ps1" -Mode %mode:~1% -GameDir "%~2"
set "result=%errorlevel%"
echo.
pause
exit /b %result%
