# Instala uma versao compilada da Frida sem encerra-la a forca: espera o operador
# fechar pelo menu Sair da bandeja, guarda backup verificado do executavel atual,
# substitui, confere o SHA-256 e reabre a Frida conferindo o heartbeat.
param([Parameter(Mandatory=$true)][string]$Release,[int]$WaitMinutes=60)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$target=Join-Path $root 'FridaAgent.exe'
$source=[IO.Path]::GetFullPath($Release)
if(!(Test-Path -LiteralPath $source)){throw "Versao nao encontrada: $source"}
$newVersion=[Reflection.AssemblyName]::GetAssemblyName($source).Version.ToString()
$oldVersion=[Reflection.AssemblyName]::GetAssemblyName($target).Version.ToString()
$deadline=(Get-Date).AddMinutes($WaitMinutes)
Write-Output "Aguardando a Frida $oldVersion ser fechada pelo menu Sair..."
while(Get-Process FridaAgent -ErrorAction SilentlyContinue | Where-Object {$_.Path -and [IO.Path]::GetFullPath($_.Path) -eq $target}){
    if((Get-Date) -gt $deadline){throw 'Tempo esgotado: a Frida continua aberta; nada foi alterado.'}
    Start-Sleep -Seconds 3
}
$backup=Join-Path (Split-Path $source -Parent) ("FridaAgent-"+$oldVersion+"-backup.exe")
Copy-Item -LiteralPath $target -Destination $backup -Force
if((Get-FileHash $backup).Hash -ne (Get-FileHash $target).Hash){throw 'Backup divergente; instalacao cancelada.'}
Copy-Item -LiteralPath $source -Destination $target -Force
if((Get-FileHash $target).Hash -ne (Get-FileHash $source).Hash){Copy-Item -LiteralPath $backup -Destination $target -Force;throw 'Copia divergente; versao anterior restaurada.'}
Write-Output "Instalado $newVersion (backup de $oldVersion em $backup)."
$process=Start-Process -FilePath $target -WorkingDirectory $root -PassThru
for($i=0;$i -lt 45;$i++){
    Start-Sleep -Seconds 2
    try{$beat=Get-Content (Join-Path $root 'frida_heartbeat.json') -Raw | ConvertFrom-Json}catch{continue}
    if($beat.pid -eq $process.Id -and $beat.version -eq $newVersion -and -not $beat.ui_stale){
        Write-Output "Frida $newVersion iniciada (PID $($process.Id), estado $($beat.state)). SHA-256 $((Get-FileHash $target).Hash)"
        exit 0
    }
}
throw "A Frida $newVersion nao confirmou o heartbeat; restaure com: Copy-Item '$backup' '$target'"
