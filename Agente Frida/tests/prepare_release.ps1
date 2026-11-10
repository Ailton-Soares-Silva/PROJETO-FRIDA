param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$OutputDirectory){
    $versionMatch=[regex]::Match((Get-Content -LiteralPath (Join-Path $root 'FridaConfig.cs') -Raw),'AssemblyVersion\("([0-9.]+)"\)')
    if(!$versionMatch.Success){throw 'Versao da aplicacao ausente.'}
    $OutputDirectory=Join-Path $root ('scratch/release-ide-'+$versionMatch.Groups[1].Value)
}
$output=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$previousBuildOutput=$env:FRIDA_BUILD_OUTPUT
try {
    $env:FRIDA_BUILD_OUTPUT=Join-Path $output 'FridaAgent.exe'
    Push-Location $root
    try { & (Join-Path $root 'compilar_frida.bat'); if($LASTEXITCODE -ne 0){throw 'Compilacao da release falhou.'} }
    finally {Pop-Location}
} finally {$env:FRIDA_BUILD_OUTPUT=$previousBuildOutput}
$names=@('FridaAgent.exe','whatsapp_reader.py','whatsapp_runtime.py','whatsapp_dom.py','frida.ico')
foreach($name in $names | Where-Object {$_ -ne 'FridaAgent.exe'}){Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $output $name) -Force}
$files=@($names | ForEach-Object {[pscustomobject]@{name=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $output $_)).Hash}})
$sourceDirectory=Join-Path $output 'sources'
[IO.Directory]::CreateDirectory($sourceDirectory) | Out-Null
$sourceNames=@([regex]::Matches((Get-Content -LiteralPath (Join-Path $root 'compilar_frida.bat') -Raw),'"([^"\r\n]+\.cs)"') | ForEach-Object {$_.Groups[1].Value}) + @('compilar_frida.bat','whatsapp_reader.py','whatsapp_runtime.py','whatsapp_dom.py')
$sources=@()
foreach($name in $sourceNames){
    $path=Join-Path $root $name
    Copy-Item -LiteralPath $path -Destination (Join-Path $sourceDirectory $name) -Force
    $sources+=[pscustomobject]@{name=$name;sha256=(Get-FileHash -LiteralPath $path).Hash}
}
$version=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $output 'FridaAgent.exe')).Version.ToString()
$package=[pscustomobject]@{schema_version=1;version=$version;created_utc=[DateTime]::UtcNow.ToString('o');files=$files;sources=$sources;required_existing_resources=@('wake','config.json');operator_validation_pending=@('phone_pairing','physical_voice','live_ssh')}
$package | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'package.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install_release.ps1') -Destination (Join-Path $output 'install_release.ps1') -Force
Write-Output "Pacote $version criado em $output"
