@echo off
REM Abre el Client de MacExtend en modo kiosk (fullscreen, sin barra de
REM direcciones ni chrome del navegador) apuntando al Server.
REM
REM Uso: editar MAC_IP con la IP de la Mac que corre el Server, y ejecutar.

set MAC_IP=192.168.1.215
set PORT=47635

start msedge --kiosk http://%MAC_IP%:%PORT%/ --edge-kiosk-type=fullscreen --no-first-run
