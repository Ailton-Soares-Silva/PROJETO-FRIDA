param([Parameter(Mandatory=$true)][string]$BaselinePath)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskBaseline=(Resolve-Path -LiteralPath $BaselinePath).Path
$taskAllowedRoot=[IO.Path]::GetFullPath((Join-Path $taskRoot 'scratch'))+[IO.Path]::DirectorySeparatorChar
if(-not $taskBaseline.StartsWith($taskAllowedRoot,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $taskBaseline -Leaf) -notlike 'baseline-functional-*'){throw 'Informe um baseline funcional dentro de scratch.'}
$taskActive=@(Get-Process -Name FridaAgent -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $taskRoot 'FridaAgent.exe')})
if($taskActive.Count -gt 0){throw 'Feche a FRIDA pelo menu Sair na bandeja antes de restaurar.'}
$taskFiles=@(Get-ChildItem -LiteralPath $taskBaseline -File | Where-Object {$_.Extension -in '.cs','.bat','.py','.exe'})
if(-not ($taskFiles | Where-Object {$_.Name -eq 'FridaAgent.exe'})){throw 'O baseline nao contem o executavel anterior.'}
foreach($taskFile in $taskFiles){Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskRoot $taskFile.Name) -Force}
Write-Output 'Codigo e executavel anteriores restaurados. Configuracao, perfis e dados atuais foram preservados.'
