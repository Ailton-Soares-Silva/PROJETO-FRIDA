@echo off
chcp 65001 >nul
setlocal
pushd "%~dp0"
if not defined FRIDA_BUILD_OUTPUT set "FRIDA_BUILD_OUTPUT=FridaAgent.exe"
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set WM=C:\Windows\System32\WinMetadata
set SP=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll
echo Compilando FridaAgent.exe (x64 Nativo Windows + WinRT Wake-Word + Holograma 3D)...
"%FW%\csc.exe" /nologo /optimize+ /platform:x64 /target:winexe /win32icon:"frida.ico" /out:"%FRIDA_BUILD_OUTPUT%" ^
  /r:System.dll /r:System.Core.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:"%SP%" ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" /r:"%FW%\System.Collections.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Globalization.winmd" /r:"%WM%\Windows.Media.winmd" ^
  "FridaConfig.cs" "FridaTeam.cs" "FridaTaskRouting.cs" "FridaSessionControl.cs" "FridaConversationAudit.cs" "FridaPrivateSources.cs" "FridaWhatsAppWorker.cs" "FridaWhatsAppResearch.cs" "FridaEconomyRouting.cs" "FridaInteractionPolicy.cs" "FridaReliability.cs" "FridaDocumentIO.cs" "FridaOperational.cs" "FridaFileSearch.cs" "FridaRuntimeHealth.cs" "FridaExecutionProtocol.cs" "FridaVerifiedTools.cs" "FridaResultForm.cs" "LocalWakeDetector.cs" "SherpaVoiceNative.cs" "LlmUsageLedger.cs" "SpeakerVoiceProfile.cs" "VoiceEnrollmentSession.cs" "VoiceEnrollmentForm.cs" "HologramOrbForm.cs" "AudioVoiceEngine.cs" "ZeldaExecutiveLlmDashboard.cs" "ZeldaConsumptionViews.cs" "AgentBrainAndTray.cs"
set "FRIDA_BUILD_EXIT=%ERRORLEVEL%"
if %FRIDA_BUILD_EXIT% EQU 0 (
    echo [OK] FridaAgent compilado com sucesso.
) else (
    echo [ERRO] Falha na compilacao.
)
popd
exit /b %FRIDA_BUILD_EXIT%
