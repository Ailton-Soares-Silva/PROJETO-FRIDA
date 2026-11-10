# Skill: Verificação de Planilhas conforme POP-PCP-001 Rev. 02
# Descrição: Valida planilhas de apontamento contra o POP-PCP-001 (Fluxo de Microprocessos Novo e Reforma).
# Uso: .\run.ps1 -target <caminho da planilha CSV ou Excel>

param(
    [Parameter(Mandatory=$true)]
    [string]$target
)

# Definir colunas obrigatórias
$requiredColumns = @(
    'Placa',
    'Chassi',
    'Fluxo',
    'Tipo de Saída',
    'Microfase',
    'Etapa',
    'Dono',
    'Data de Início',
    'Data de Saída',
    'OS'
)

# Valores permitidos
$allowedFluxos = @('Novo','Reforma')
$allowedTiposSaida = @('Novo','Reforma','Outro')
$allowedMicrofases = $allowedFluxos

# Mapeamento de Etapa -> LeadTime (dias) e Dono padrão (extraído do POP-PCP-001 Rev.02)
$stepInfo = @{
    'Fabricação da caixa' = @{LeadTime = 1; Owner = 'Paulo'}
    'Cilindros' = @{LeadTime = 0.5; Owner = 'Paulo'}
    'Frente paralela' = @{LeadTime = 0.5; Owner = 'Paulo'}
    'Hidráulica do implemento' = @{LeadTime = 1; Owner = 'Paulo'}
    'Elétrica do implemento' = @{LeadTime = 1; Owner = 'Paulo'}
    'Encarroçamento' = @{LeadTime = 0.5; Owner = 'Paulo'}
    'Roll-on / poliguindaste' = @{LeadTime = 20; Owner = 'Paulo'}
    'Lava-jato' = @{LeadTime = 0.04; Owner = 'Paulo'}
    'Mecânica' = @{LeadTime = 7; Owner = 'Mônica'}
    'Elétrica do chassi' = @{LeadTime = 1; Owner = 'Mônica'}
    'Entre-eixo' = @{LeadTime = 2; Owner = 'Mônica'}
    'Desencarroçamento' = @{LeadTime = 0; Owner = 'Paulo'}
    'Reforma da caixa' = @{LeadTime = 4; Owner = 'Paulo'}
    'Pintura da caixa' = @{LeadTime = 1; Owner = 'Paulo'}
    'Pintura de cabine' = @{LeadTime = 1; Owner = 'Wanderson'}
    'Vistoria final' = @{LeadTime = 1; Owner = 'Luciano'}
    'Liberação e entrega' = @{LeadTime = 1; Owner = 'Vitória'}
    'Centralização da demanda' = @{LeadTime = 1; Owner = 'Jordana'}
    'Validação de prazo' = @{LeadTime = 1; Owner = 'Mônica'}
    'Liberação da OS' = @{LeadTime = 0.1; Owner = 'Mônica'}
    'Corte e dobra' = @{LeadTime = 2; Owner = 'Sueide'}
    'Recepção do chassi' = @{LeadTime = 1; Owner = 'Paulo'}
    'Terceiro eixo' = @{LeadTime = 10; Owner = 'Fornecedor'}
    'Retoque de pintura da caixa' = @{LeadTime = 1; Owner = 'Wanderson'}
}

function Parse-Date($dateStr) {
    if ([string]::IsNullOrWhiteSpace($dateStr)) { return $null }
    try {
        return [datetime]::Parse($dateStr)
    } catch {
        return $null
    }
}

# Carregar planilha (CSV ou Excel)
$extension = [System.IO.Path]::GetExtension($target).ToLower()
if ($extension -eq '.csv') {
    $rows = Import-Csv -Path $target -Encoding UTF8
} elseif ($extension -in @('.xlsx','.xls')) {
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = $false
    $workbook = $excel.Workbooks.Open($target)
    $sheet = $workbook.Sheets.Item(1)
    $used = $sheet.UsedRange
    $data = $used.Value2
    $header = $data[1]
    $rows = @()
    for ($i = 2; $i -le $used.Rows.Count; $i++) {
        $obj = @{}
        for ($j = 1; $j -le $used.Columns.Count; $j++) {
            $colName = $header[$j]
            $obj[$colName] = $data[$i][$j]
        }
        $rows += New-Object PSObject -Property $obj
    }
    $workbook.Close($false)
    $excel.Quit()
    [System.Runtime.Interopservices.Marshal]::ReleaseComObject($excel) | Out-Null
} else {
    Write-Error "Formato de arquivo não suportado: $extension"
    exit 1
}

# Verificar colunas obrigatórias
$actualColumns = $rows[0].PSObject.Properties.Name
$missingCols = $requiredColumns | Where-Object { $actualColumns -notcontains $_ }
if ($missingCols) {
    Write-Error "Colunas obrigatórias ausentes: $($missingCols -join ', ')"
    exit 1
}

$errors = @()
$warnings = @()
$lineNumber = 2
foreach ($row in $rows) {
    $line = $lineNumber
    if ($allowedFluxos -notcontains $row.Fluxo) {
        $errors += "Linha $line: Valor de Fluxo inválido '$($row.Fluxo)'. Permitidos: $($allowedFluxos -join ', ')"
    }
    if ($allowedTiposSaida -notcontains $row.'Tipo de Saída') {
        $errors += "Linha $line: Tipo de Saída inválido '$($row.'Tipo de Saída')'."
    }
    if ($allowedMicrofases -notcontains $row.Microfase) {
        $errors += "Linha $line: Microfase inválida '$($row.Microfase)'."
    }
    if (-not $stepInfo.ContainsKey($row.Etapa)) {
        $warnings += "Linha $line: Etapa desconhecida '$($row.Etapa)'."
    } else {
        $expectedOwner = $stepInfo[$row.Etapa].Owner
        if ($row.Dono -and $row.Dono -ne $expectedOwner) {
            $warnings += "Linha $line: Dono da etapa '$($row.Etapa)' esperado '$expectedOwner', encontrado '$($row.Dono)'."
        }
        $start = Parse-Date $row.'Data de Início'
        $end = Parse-Date $row.'Data de Saída'
        if ($start -and $end) {
            $actualDays = ($end - $start).TotalDays
            $refDays = $stepInfo[$row.Etapa].LeadTime
            if ($actualDays -gt $refDays) {
                $warnings += "Linha $line: Lead time da etapa '$($row.Etapa)' excede o referência ($([math]::Round($actualDays,2)) > $refDays dias)."
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($row.OS) -and $row.'Data de Início') {
        $errors += "Linha $line: OS ausente, mas Data de Início preenchida."
    }
    if (-not [string]::IsNullOrWhiteSpace($row.'Data de Saída') -and [string]::IsNullOrWhiteSpace($row.Etapa)) {
        $errors += "Linha $line: Data de Saída preenchida sem Etapa definida."
    }
    $lineNumber++
}

$reportPath = [System.IO.Path]::ChangeExtension($target, '.verificacao.txt')
$report = @()
$report += "=== Relatório de Verificação POP-PCP-001 ==="
$report += "Arquivo analisado: $target"
$report += "Data da verificação: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$report += ""
$report += "Total de linhas analisadas: $($rows.Count)"
$report += "Erros encontrados: $($errors.Count)"
$report += "Avisos encontrados: $($warnings.Count)"
$report += ""
if ($errors) {
    $report += "--- Erros ---"
    $report += $errors
}
if ($warnings) {
    $report += "--- Avisos ---"
    $report += $warnings
}
$report | Set-Content -Path $reportPath -Encoding UTF8

Write-Output "Verificação concluída. Relatório salvo em: $reportPath"
if ($errors) {
    Write-Error "Foram encontrados $($errors.Count) erro(s). Consulte o relatório."
    exit 1
} else {
    Write-Output "Nenhum erro crítico encontrado."
    exit 0
}
