param([string]$Candidate)
$ErrorActionPreference='Stop'
$project=Split-Path $PSScriptRoot -Parent
if(!$Candidate){$Candidate=Join-Path $project 'scratch/FridaIdeCandidate.exe'}
$fixture=Join-Path $project ('scratch/release-tests-'+[Guid]::NewGuid().ToString('N'))
$target=Join-Path $fixture 'target'
$package=Join-Path $fixture 'package'
[IO.Directory]::CreateDirectory($target) | Out-Null
[IO.Directory]::CreateDirectory($package) | Out-Null
$installer=Join-Path $PSScriptRoot 'install_release.ps1'
$original=[Text.Encoding]::UTF8.GetBytes('OLD EXECUTABLE')
[IO.File]::WriteAllBytes((Join-Path $target 'FridaAgent.exe'),$original)
[IO.File]::WriteAllText((Join-Path $target 'whatsapp_reader.py'),'ORIGINAL PYTHON')
[IO.File]::WriteAllText((Join-Path $target 'config.json'),'USER CONFIGURATION SENTINEL')
$configHash=(Get-FileHash -LiteralPath (Join-Path $target 'config.json')).Hash
$oldExeHash=(Get-FileHash -LiteralPath (Join-Path $target 'FridaAgent.exe')).Hash
Copy-Item -LiteralPath $Candidate -Destination (Join-Path $package 'FridaAgent.exe')
$names=@('FridaAgent.exe','whatsapp_reader.py','whatsapp_runtime.py','whatsapp_dom.py')
foreach($name in $names | Where-Object {$_ -ne 'FridaAgent.exe'}){Copy-Item -LiteralPath (Join-Path $project $name) -Destination (Join-Path $package $name)}
$files=@($names | ForEach-Object {[pscustomobject]@{name=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $package $_)).Hash}})
$manifest=Join-Path $package 'package.json'
[pscustomobject]@{schema_version=1;files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifest -Encoding UTF8
function Check([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function Invoke-Install {
    $previousPreference=$ErrorActionPreference
    $ErrorActionPreference='Continue'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Release (Join-Path $package 'FridaAgent.exe') -PackageManifest $manifest -TargetDirectory $target -NoStart *> (Join-Path $fixture 'last-install.log')
    $code=$LASTEXITCODE
    $ErrorActionPreference=$previousPreference
    return $code
}
Check ((Invoke-Install) -eq 0) 'Falha ao instalar pacote isolado.'
foreach($item in $files){Check ((Get-FileHash -LiteralPath (Join-Path $target $item.name)).Hash -eq $item.sha256) 'Hash instalado diverge.'}
Check ((Get-FileHash -LiteralPath (Join-Path $target 'config.json')).Hash -eq $configHash) 'Configuracao do usuario foi alterada.'
$backup=Get-ChildItem -LiteralPath $package -Directory -Filter 'backup-*' | Select-Object -First 1
$rollback=Join-Path $backup.FullName 'rollback.json'
& powershell -NoProfile -ExecutionPolicy Bypass -File $installer -RollbackManifest $rollback -TargetDirectory $target -NoStart *> (Join-Path $fixture 'rollback.log')
Check ($LASTEXITCODE -eq 0) 'Restauracao falhou.'
Check ((Get-FileHash -LiteralPath (Join-Path $target 'FridaAgent.exe')).Hash -eq $oldExeHash) 'Executavel anterior nao foi restaurado.'
Check (!(Test-Path -LiteralPath (Join-Path $target 'whatsapp_dom.py'))) 'Arquivo novo permaneceu apos rollback.'
Check ((Get-FileHash -LiteralPath (Join-Path $target 'config.json')).Hash -eq $configHash) 'Rollback alterou configuracao.'
[IO.File]::AppendAllText((Join-Path $package 'whatsapp_dom.py'),'TAMPER')
Check ((Invoke-Install) -ne 0) 'Pacote adulterado foi aceito.'
Check ((Get-FileHash -LiteralPath (Join-Path $target 'FridaAgent.exe')).Hash -eq $oldExeHash) 'Falha de integridade alterou instalacao.'
Copy-Item -LiteralPath (Join-Path $project 'whatsapp_dom.py') -Destination (Join-Path $package 'whatsapp_dom.py') -Force
$locked=Join-Path $target 'whatsapp_reader.py'
(Get-Item -LiteralPath $locked).IsReadOnly=$true
try {Check ((Invoke-Install) -ne 0) 'Arquivo protegido deveria impedir atualizacao.'} finally {(Get-Item -LiteralPath $locked).IsReadOnly=$false}
Check ((Get-FileHash -LiteralPath (Join-Path $target 'FridaAgent.exe')).Hash -eq $oldExeHash) 'Falha parcial nao restaurou o executavel.'
$report=[pscustomobject]@{install_verified=$true;rollback_verified=$true;configuration_preserved=$true;tamper_rejected=$true;partial_failure_rolled_back=$true;fixture=$fixture}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'results/ide-installer-tests.json') -Encoding UTF8
$report | ConvertTo-Json
