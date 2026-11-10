# Atendimento real de ponta a ponta: cerebro de producao, config.json (somente leitura),
# modelos seniores qualificados e worker Python com o perfil autenticado do WhatsApp.
# Faz chamadas reais aos provedores e le o WhatsApp do operador.
param([string]$Requests = "$PSScriptRoot\whatsapp_e2e_requests.txt")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
# O worker localiza os scripts Python na pasta do executavel; o harness roda da raiz.
$exe = Join-Path $root 'FridaWhatsAppE2E.exe'
try {
    $fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'; $wm = 'C:\Windows\System32\WinMetadata'
    $sources = [regex]::Matches((Get-Content -LiteralPath 'compilar_frida.bat' -Raw), '"([^"\r\n]+\.cs)"') | ForEach-Object { $_.Groups[1].Value }
    $csc = @('/nologo','/platform:x64','/target:exe','/main:FridaAgent.WhatsAppTests',('/out:'+$exe),'/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Net.Http.dll','/r:Microsoft.VisualBasic.dll','/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',('/r:'+$fw+'\System.Runtime.dll'),('/r:'+$fw+'\System.Collections.dll'),('/r:'+$fw+'\System.Runtime.InteropServices.WindowsRuntime.dll'),'/r:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll',('/r:'+$wm+'\Windows.Foundation.winmd'),('/r:'+$wm+'\Windows.Globalization.winmd'),('/r:'+$wm+'\Windows.Media.winmd')) + $sources + @('tests\FunctionalTests.cs','tests\WhatsAppTests.cs','tests\CodingCopilotTests.cs','tests\IdeTests.cs')
    & ($fw+'\csc.exe') $csc
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o harness E2E.' }
    $out = Join-Path $PSScriptRoot 'results\whatsapp-e2e.json'
    & $exe --e2e $root $Requests $out
    if ($LASTEXITCODE -ne 0) { throw 'Falha no atendimento E2E.' }
} finally {
    if (Test-Path $exe) { Remove-Item -LiteralPath $exe -Force }
    Pop-Location
}
