@echo off
chcp 65001 >nul
setlocal
pushd "%~dp0"
tasklist /FI "IMAGENAME eq FridaAgent.exe" 2>nul | find /I "FridaAgent.exe" >nul
if %ERRORLEVEL% EQU 0 (
    echo [AVISO] Encerrando instancia legada FridaAgent.exe para liberar o microfone...
    taskkill /IM FridaAgent.exe /F >nul 2>&1
)
start "" "%~dp0FridaAgyAgent.exe"
echo [OK] FridaAgyAgent.exe iniciado na bandeja do sistema (F4 = Voz, Ctrl+Alt+F = Console).
popd
exit /b 0

