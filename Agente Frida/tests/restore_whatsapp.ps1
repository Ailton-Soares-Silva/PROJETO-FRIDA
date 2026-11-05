$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$taskBaseline=Join-Path $taskRoot 'scratch/baseline-whatsapp-20261104-172454'
if(!(Test-Path -LiteralPath (Join-Path $taskBaseline 'manifest.json'))){throw 'Baseline nao encontrado.'}
$taskRunning=Get-Process FridaAgent -ErrorAction SilentlyContinue | Where-Object {$_.Path -and ([IO.Path]::GetFullPath($_.Path)).StartsWith($taskRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)}
if($taskRunning){throw 'Encerre a Frida pelo menu Sair da bandeja antes de restaurar.'}
foreach($taskFile in Get-ChildItem -LiteralPath $taskBaseline -File){
    if($taskFile.Name -eq 'manifest.json'){continue}
    $taskTarget=Join-Path $taskRoot $taskFile.Name
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTarget -Force
}
foreach($taskFile in Get-ChildItem -LiteralPath (Join-Path $taskBaseline 'tests') -File){
    Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $PSScriptRoot $taskFile.Name) -Force
}
Write-Output 'Baseline restaurado. Perfil, cache e credenciais atuais foram preservados.'
