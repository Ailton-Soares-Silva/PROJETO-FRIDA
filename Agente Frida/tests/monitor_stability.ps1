param([ValidateRange(1,1440)][int]$Minutes=120)
$ErrorActionPreference='Stop'
function Convert-TaskUtc($Value){
    if($Value -is [DateTime]){return $Value.ToUniversalTime()}
    return [DateTime]::Parse([string]$Value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
}
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskHeartbeat=Join-Path $taskRoot 'frida_heartbeat.json'
$taskExe=Join-Path $taskRoot 'FridaAgent.exe'
$taskStarted=[DateTime]::UtcNow
$taskClock=[Diagnostics.Stopwatch]::StartNew()
$taskSamples=[Collections.Generic.List[object]]::new()
$taskErrors=[Collections.Generic.List[string]]::new()
$taskPid=$null
$taskInitialCycles=$null
$taskLastCycles=0
Write-Output "Monitorando a FRIDA por $Minutes minutos. O teste completo exige 30 atendimentos reais, incluindo texto e voz, e 10 ciclos de inatividade."
try {
    while($taskClock.Elapsed.TotalMinutes -lt $Minutes){
        $taskBeat=Get-Content -LiteralPath $taskHeartbeat -Raw | ConvertFrom-Json
        if($null -eq $taskPid){$taskPid=[int]$taskBeat.pid;$taskInitialCycles=[int]$taskBeat.inactivity_cycles}
        if([int]$taskBeat.pid -ne $taskPid){throw 'O processo foi substituido durante a observacao.'}
        $taskProcess=Get-Process -Id $taskPid -ErrorAction Stop
        if($taskProcess.Path -ne $taskExe){throw 'O heartbeat nao pertence ao executavel esperado.'}
        $taskAge=([DateTime]::UtcNow-(Convert-TaskUtc $taskBeat.heartbeat_utc)).TotalSeconds
        $taskUiAge=([DateTime]::UtcNow-(Convert-TaskUtc $taskBeat.last_ui_utc)).TotalSeconds
        if($taskAge -gt 45 -or $taskUiAge -gt 45 -or $taskBeat.ui_stale){throw 'Heartbeat ou interface sem resposta por mais de 45 segundos.'}
        $taskLastCycles=[int]$taskBeat.inactivity_cycles
        $taskSamples.Add(@{utc=[DateTime]::UtcNow.ToString('o');pid=$taskPid;state=$taskBeat.state;heartbeat_age_seconds=$taskAge;ui_age_seconds=$taskUiAge;inactivity_cycles=$taskLastCycles})
        Start-Sleep -Seconds 5
    }
} catch {$taskErrors.Add($_.Exception.Message)}
$taskConversations=@(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'ConversationReports') -Filter '*.json' -ErrorAction SilentlyContinue | Where-Object {$_.LastWriteTimeUtc -ge $taskStarted} | ForEach-Object {try{$taskEntry=Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json;if((Convert-TaskUtc $taskEntry.started_utc) -ge $taskStarted){$taskEntry}}catch{}})
$taskText=@($taskConversations | Where-Object {$_.input_channel -eq 'text'}).Count
$taskVoice=@($taskConversations | Where-Object {$_.input_channel -eq 'voice'}).Count
$taskComplete=($taskClock.Elapsed.TotalMinutes -ge 120 -and $taskErrors.Count -eq 0 -and $taskConversations.Count -ge 30 -and $taskText -gt 0 -and $taskVoice -gt 0 -and ($taskLastCycles-$taskInitialCycles) -ge 10)
$taskReport=@{started_utc=$taskStarted.ToString('o');duration_minutes=$taskClock.Elapsed.TotalMinutes;executable_sha256=(Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash;stability_criteria_met=$taskComplete;functional_results_require_manual_review=$true;text_turns=$taskText;voice_turns=$taskVoice;inactivity_cycles=($taskLastCycles-$taskInitialCycles);errors=$taskErrors.ToArray();samples=$taskSamples.ToArray()}
$taskResult=Join-Path $PSScriptRoot ('results\stability-'+$taskStarted.ToString('yyyyMMdd-HHmmss')+'.json')
[IO.Directory]::CreateDirectory((Split-Path $taskResult -Parent)) | Out-Null
$taskReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskResult -Encoding utf8
Write-Output "Relatorio: $taskResult"
Write-Output "Criterios completos de estabilidade cumpridos: $taskComplete"
if($taskErrors.Count -gt 0){throw ($taskErrors -join '; ')}
