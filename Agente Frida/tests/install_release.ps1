param(
    [string]$Release,
    [string]$PackageManifest,
    [string]$RollbackManifest,
    [string]$TargetDirectory,
    [ValidateRange(0,120)][int]$WaitMinutes=60,
    [switch]$NoStart
)
$ErrorActionPreference='Stop'
$allowed=@('FridaAgent.exe','whatsapp_reader.py','whatsapp_runtime.py','whatsapp_dom.py','frida.ico')
if(!$TargetDirectory){$TargetDirectory=Split-Path $PSScriptRoot -Parent}
$root=[IO.Path]::GetFullPath($TargetDirectory)
if(!(Test-Path -LiteralPath $root -PathType Container)){throw 'Diretorio de instalacao inexistente.'}
$target=Join-Path $root 'FridaAgent.exe'
function Get-ReleasePath([string]$Name){
    if($allowed -notcontains $Name -or [IO.Path]::GetFileName($Name) -ne $Name){throw "Arquivo fora do pacote permitido: $Name"}
    return Join-Path $root $Name
}
function Wait-ReleaseStopped {
    $deadline=(Get-Date).AddMinutes($WaitMinutes)
    while(Get-Process FridaAgent -ErrorAction SilentlyContinue | Where-Object {$_.Path -and [IO.Path]::GetFullPath($_.Path) -eq $target}){
        if((Get-Date) -ge $deadline){throw 'A Frida continua aberta. Feche pelo menu Sair antes de atualizar.'}
        Start-Sleep -Seconds 3
    }
}
function Set-ReleaseFile([string]$Source,[string]$Destination){
    $temporary=$Destination+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    $replaced=$Destination+'.'+[Guid]::NewGuid().ToString('N')+'.previous'
    try {
        Copy-Item -LiteralPath $Source -Destination $temporary
        if((Get-FileHash -LiteralPath $Source).Hash -ne (Get-FileHash -LiteralPath $temporary).Hash){throw 'Copia temporaria divergente.'}
        if(Test-Path -LiteralPath $Destination){[IO.File]::Replace($temporary,$Destination,$replaced)}else{[IO.File]::Move($temporary,$Destination)}
    } finally {
        if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}
        if(Test-Path -LiteralPath $replaced){Remove-Item -LiteralPath $replaced}
    }
}
function Restore-Release($Manifest,[string]$BackupDirectory){
    if([IO.Path]::GetFullPath($Manifest.target_directory) -ne $root){throw 'O backup pertence a outra instalacao.'}
    foreach($record in $Manifest.files){
        $destination=Get-ReleasePath $record.name
        if(Test-Path -LiteralPath $destination){
            $current=(Get-FileHash -LiteralPath $destination).Hash
            if($current -ne $record.installed_sha256 -and $current -ne $record.previous_sha256){throw "O arquivo mudou apos a atualizacao: $($record.name). Preserve essa alteracao antes de restaurar."}
        }
        if($record.existed){
            $backup=Join-Path $BackupDirectory $record.name
            if(!(Test-Path -LiteralPath $backup) -or (Get-FileHash -LiteralPath $backup).Hash -ne $record.previous_sha256){throw 'Backup ausente ou divergente.'}
        }
    }
    foreach($record in $Manifest.files){
        $destination=Get-ReleasePath $record.name
        if($record.existed){
            if((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination).Hash -eq $record.previous_sha256){continue}
            Set-ReleaseFile (Join-Path $BackupDirectory $record.name) $destination
        }
        elseif(Test-Path -LiteralPath $destination){Remove-Item -LiteralPath $destination}
    }
}
if($RollbackManifest){
    $rollback=[IO.Path]::GetFullPath($RollbackManifest)
    $manifest=Get-Content -LiteralPath $rollback -Raw | ConvertFrom-Json
    Wait-ReleaseStopped
    Restore-Release $manifest (Split-Path $rollback -Parent)
    Write-Output 'Versao anterior restaurada com configuracoes e dados atuais preservados.'
    if(!$NoStart){Start-Process -FilePath $target -WorkingDirectory $root -WindowStyle Hidden | Out-Null}
    exit 0
}
if(!$Release){throw 'Informe -Release ou -RollbackManifest.'}
$source=[IO.Path]::GetFullPath($Release)
$newVersion=[Reflection.AssemblyName]::GetAssemblyName($source).Version.ToString()
$payload=@([pscustomobject]@{name='FridaAgent.exe';sha256=(Get-FileHash -LiteralPath $source).Hash;source=$source})
if($PackageManifest){
    $packagePath=[IO.Path]::GetFullPath($PackageManifest)
    $package=Get-Content -LiteralPath $packagePath -Raw | ConvertFrom-Json
    $directory=Split-Path $packagePath -Parent
    $payload=@()
    foreach($item in $package.files){
        $null=Get-ReleasePath $item.name
        $file=Join-Path $directory $item.name
        if(!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $item.sha256){throw "Pacote divergente: $($item.name)"}
        $payload+=[pscustomobject]@{name=$item.name;sha256=$item.sha256;source=$file}
    }
    if(@($payload | Where-Object {$_.name -eq 'FridaAgent.exe' -and $_.sha256 -eq (Get-FileHash -LiteralPath $source).Hash}).Count -ne 1){throw 'O executavel informado diverge do pacote.'}
    if(@($payload.name | Select-Object -Unique).Count -ne $payload.Count){throw 'Pacote com arquivos duplicados.'}
}
Wait-ReleaseStopped
$backupDirectory=Join-Path (Split-Path $source -Parent) ('backup-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
[IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
$records=@()
foreach($item in $payload){
    $destination=Get-ReleasePath $item.name
    $existed=Test-Path -LiteralPath $destination
    $previous=$null
    if($existed){
        $previous=(Get-FileHash -LiteralPath $destination).Hash
        $backup=Join-Path $backupDirectory $item.name
        Copy-Item -LiteralPath $destination -Destination $backup
        if((Get-FileHash -LiteralPath $backup).Hash -ne $previous){throw 'Backup divergente. Instalacao cancelada.'}
    }
    $records+=[pscustomobject]@{name=$item.name;existed=[bool]$existed;previous_sha256=$previous;installed_sha256=$item.sha256}
}
# The installer never edits conversation data, but a new version may migrate it on first start.
# Snapshot it beside the executable backup as one zip per folder: workspace message paths are deep,
# and a plain copy nested under the backup directory would exceed MAX_PATH (260).
# Restore manually with Frida closed: extract each data-<pasta>.zip into <pasta> of the installation.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$dataBackup=Join-Path $backupDirectory 'data'
foreach($folder in @('IdeData','ConversationReports')){
    $sourceFolder=Join-Path $root $folder
    if(Test-Path -LiteralPath $sourceFolder -PathType Container){
        [IO.Directory]::CreateDirectory($dataBackup) | Out-Null
        $archive=Join-Path $dataBackup ('data-'+$folder+'.zip')
        [IO.Compression.ZipFile]::CreateFromDirectory($sourceFolder,$archive,[IO.Compression.CompressionLevel]::Optimal,$false)
        $expected=@(Get-ChildItem -LiteralPath $sourceFolder -Recurse -File -Force).Count
        $zip=[IO.Compression.ZipFile]::OpenRead($archive)
        try{$stored=@($zip.Entries | Where-Object {$_.Name -ne ''}).Count}finally{$zip.Dispose()}
        if($stored -ne $expected){throw "Backup de dados incompleto: $folder ($stored de $expected arquivos). Instalacao cancelada."}
    }
}
$backupManifest=[pscustomobject]@{schema_version=1;target_directory=$root;version=$newVersion;files=$records;data_backup=$(if(Test-Path -LiteralPath $dataBackup){$dataBackup}else{$null})}
$rollbackPath=Join-Path $backupDirectory 'rollback.json'
$backupManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $rollbackPath -Encoding UTF8
try {
    foreach($item in $payload){
        $destination=Get-ReleasePath $item.name
        if((Test-Path -LiteralPath $destination) -and (Get-Item -LiteralPath $destination).IsReadOnly){throw "Arquivo protegido contra gravacao: $($item.name)"}
        Set-ReleaseFile $item.source $destination
        if((Get-FileHash -LiteralPath $destination).Hash -ne $item.sha256){throw 'Conferencia apos instalacao falhou.'}
    }
} catch {
    $failure=$_
    Restore-Release $backupManifest $backupDirectory
    throw $failure
}
Write-Output "Instalado $newVersion. Restauracao: $rollbackPath"
if($NoStart){exit 0}
$process=Start-Process -FilePath $target -WorkingDirectory $root -WindowStyle Hidden -PassThru
for($index=0;$index -lt 45;$index++){
    Start-Sleep -Seconds 2
    try{$beat=Get-Content -LiteralPath (Join-Path $root 'frida_heartbeat.json') -Raw | ConvertFrom-Json}catch{continue}
    if($beat.pid -eq $process.Id -and $beat.version -eq $newVersion -and -not $beat.ui_stale){
        Write-Output "Heartbeat confirmado. PID $($process.Id). SHA-256 $((Get-FileHash -LiteralPath $target).Hash)"
        exit 0
    }
}
throw "A Frida nao confirmou o heartbeat. Backup disponivel em $rollbackPath"
