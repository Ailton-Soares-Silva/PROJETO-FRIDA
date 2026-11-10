$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$metadata = 'C:\Windows\System32\WinMetadata'
Push-Location $root
try {
    $sources = [regex]::Matches((Get-Content -LiteralPath 'compilar_frida.bat' -Raw), '"([^"\r\n]+\.cs)"') | ForEach-Object { $_.Groups[1].Value }
    $arguments = @('/nologo','/platform:x64','/target:exe','/main:FridaAgent.VoiceEvaluation','/out:FridaVoiceEvaluation.exe',
        '/r:Microsoft.VisualBasic.dll','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',
        '/r:System.Net.Http.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',
        ('/r:'+$framework+'\System.Runtime.dll'),('/r:'+$framework+'\System.Collections.dll'),
        ('/r:'+$framework+'\System.Runtime.InteropServices.WindowsRuntime.dll'),
        '/r:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll',
        ('/r:'+$metadata+'\Windows.Foundation.winmd'),('/r:'+$metadata+'\Windows.Globalization.winmd'),('/r:'+$metadata+'\Windows.Media.winmd'))
    & ($framework+'\csc.exe') ($arguments + $sources + @('tests\VoiceRecognitionTests.cs','tests\VoiceEvaluation.cs'))
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar avaliador de voz' }
} finally { Pop-Location }
