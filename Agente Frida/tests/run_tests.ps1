param([switch]$DriveSmoke,[switch]$WindowsSmoke,[switch]$QualifyWhatsApp)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    $taskCompilerDir = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
    $taskMetadataDir = 'C:\Windows\System32\WinMetadata'
    $taskManifest = Get-Content -LiteralPath 'compilar_frida.bat' -Raw
    $taskSources = [regex]::Matches($taskManifest, '"([^"\r\n]+\.cs)"') | ForEach-Object { $_.Groups[1].Value }
    $taskOutput = Join-Path $PSScriptRoot $(if($QualifyWhatsApp){'WhatsAppTests.exe'}else{'FunctionalTests.exe'})
    $taskMain = if($QualifyWhatsApp){'/main:FridaAgent.WhatsAppTests'}else{'/main:FridaAgent.FunctionalTests'}
    $taskArgs = @('/nologo','/platform:x64','/target:exe',$taskMain,('/out:'+$taskOutput),'/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Net.Http.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',('/r:'+ $taskCompilerDir+'\System.Runtime.dll'),('/r:'+ $taskCompilerDir+'\System.Collections.dll'),('/r:'+ $taskCompilerDir+'\System.Runtime.InteropServices.WindowsRuntime.dll'),'/r:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll',('/r:'+$taskMetadataDir+'\Windows.Foundation.winmd'),('/r:'+$taskMetadataDir+'\Windows.Globalization.winmd'),('/r:'+$taskMetadataDir+'\Windows.Media.winmd')) + $taskSources + @('tests\FunctionalTests.cs','tests\WhatsAppTests.cs')
    & ($taskCompilerDir+'\csc.exe') $taskArgs
    if($LASTEXITCODE -ne 0){throw 'Falha ao compilar os testes C#.'}
    $taskOptions = @()
    if($QualifyWhatsApp){$taskOptions=@("--qualify",$taskRoot)}
    if($DriveSmoke){$taskOptions += "--drive-smoke"}
    if($WindowsSmoke){$taskOptions += "--windows-smoke"}
    & $taskOutput @taskOptions
    if($LASTEXITCODE -ne 0){throw 'Falha nos testes C#.'}
    if($QualifyWhatsApp){return}
    python -m unittest discover -s tests -p 'test_*.py' -v
    if($LASTEXITCODE -ne 0){throw 'Falha nos testes Python.'}
} finally { Pop-Location }
