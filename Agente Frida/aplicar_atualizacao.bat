@echo off
chcp 65001 >nul
pushd "%~dp0"
echo Encerrando processo ativo FridaAgent...
taskkill /f /im FridaAgent.exe >nul 2>&1
timeout /t 1 /nobreak >nul

if exist "FridaAgent.update.exe" (
    move /y "FridaAgent.update.exe" "FridaAgent.exe"
    echo [OK] FridaAgent.exe atualizado.
)
if exist "FridaAgyAgent.update.exe" (
    move /y "FridaAgyAgent.update.exe" "FridaAgyAgent.exe"
    echo [OK] FridaAgyAgent.exe atualizado.
)

echo Iniciando FridaAgent atualizado...
start "" "FridaAgent.exe"
echo [OK] Frida reiniciada com sucesso.
popd
