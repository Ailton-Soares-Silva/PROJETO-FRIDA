@echo off
chcp 65001 >nul
setlocal
pushd "%~dp0"
if not defined FRIDA_AGY_OUTPUT set "FRIDA_AGY_OUTPUT=FridaAgyAgent.exe"
set FW=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set WM=C:\Windows\System32\WinMetadata
set SP=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll
set WB=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll
set UIA=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll
set UIAT=C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll
echo Compilando %FRIDA_AGY_OUTPUT% (Voz + Orbe 3D + Ponte Google Antigravity SDK)...
"%FW%\csc.exe" /nologo /optimize+ /platform:x64 /target:winexe /main:FridaAgent.FridaAgyProgram /win32icon:"frida.ico" /out:"%FRIDA_AGY_OUTPUT%" ^
  /r:Microsoft.VisualBasic.dll /r:System.dll /r:System.Core.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:"%SP%" /r:"%WB%" /r:"%UIA%" /r:"%UIAT%" ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Runtime.InteropServices.WindowsRuntime.dll" /r:"%FW%\System.Collections.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Globalization.winmd" /r:"%WM%\Windows.Media.winmd" ^
  "FridaConfig.cs" "FridaWindowVerification.cs" "FridaTeam.cs" "FridaModelCatalog.cs" "FridaTaskRouting.cs" "FridaPlanner.cs" "FridaWebTools.cs" "FridaMemoryStore.cs" "FridaSkillSystem.cs" "FridaBaseSpreadsheets.cs" "FridaSessionControl.cs" "FridaConversationAudit.cs" "FridaPrivateSources.cs" "FridaWhatsAppWorker.cs" "FridaWhatsAppResearch.cs" "FridaEconomyRouting.cs" "FridaInteractionPolicy.cs" "FridaReliability.cs" "FridaDocumentIO.cs" "FridaOperational.cs" "FridaFileSearch.cs" "FridaRuntimeHealth.cs" "FridaExecutionProtocol.cs" "FridaVerifiedTools.cs" "FridaCodingCopilot.cs" "FridaIdeRuntime.cs" "FridaConversationStore.cs" "FridaConversationMigration.cs" "FridaConversationBrain.cs" "FridaWorkspaceForm.cs" "FridaUiKit.cs" "FridaWorkspaceRouting.cs" "FridaMeetingArtifacts.cs" "FridaDiagnostics.cs" "FridaZeldaAccessibility.cs" "FridaSshWorkspace.cs" "FridaIdeForm.cs" "FridaChatLayout.cs" "FridaCodeEditor.cs" "FridaResultForm.cs" "LocalWakeDetector.cs" "SherpaVoiceNative.cs" "LlmUsageLedger.cs" "SpeakerVoiceProfile.cs" "VoiceEnrollmentSession.cs" "VoiceEnrollmentForm.cs" "HologramOrbForm.cs" "FridaSpeechRecognition.cs" "AudioVoiceEngine.cs" "ZeldaExecutiveLlmDashboard.cs" "ZeldaConsumptionViews.cs" "AgentBrainAndTray.cs" "FridaAgyVoiceShell.cs"
set "FRIDA_AGY_EXIT=%ERRORLEVEL%"
if %FRIDA_AGY_EXIT% EQU 0 (
    echo [OK] %FRIDA_AGY_OUTPUT% compilado com sucesso.
) else (
    echo [ERRO] Falha na compilacao de %FRIDA_AGY_OUTPUT%.
)
popd
exit /b %FRIDA_AGY_EXIT%

