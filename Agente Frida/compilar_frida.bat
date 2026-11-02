@echo off
chcp 65001 >nul
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set WM=C:\Windows\System32\WinMetadata
set SP=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll
echo Compilando FridaAgent.exe (x64 Nativo Windows + WinRT Wake-Word + Holograma 3D)...
"%FW%\csc.exe" /nologo /optimize+ /platform:x64 /target:winexe /win32icon:"frida.ico" /out:"FridaAgent.exe" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:"%SP%" ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" /r:"%FW%\System.Collections.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Globalization.winmd" /r:"%WM%\Windows.Media.winmd" ^
  "FridaConfig.cs" "LlmUsageLedger.cs" "SpeakerVoiceProfile.cs" "HologramOrbForm.cs" "AudioVoiceEngine.cs" "ZeldaExecutiveLlmDashboard.cs" "ZeldaConsumptionViews.cs" "AgentBrainAndTray.cs"
if %ERRORLEVEL% EQU 0 (
    echo [OK] FridaAgent.exe compilado com sucesso.
) else (
    echo [ERRO] Falha na compilacao.
)
