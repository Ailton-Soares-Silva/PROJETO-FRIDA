# Consolidar Placas Pendentes de Mecânica do Google Drive e WhatsApp em Excel/CSV

## Descrição
Lê e processa as 3 planilhas oficiais do Google Drive e cruza com menções no cache SQLite do WhatsApp (FTS5). Classifica chassis e placas em tempo real de acordo com as regras de processo do **POP-PCP-001 Rev 02 (Fluxo de Microprocessos COPAC)**, identificando o fluxo (`NOVO`, `REFORMA`, `SO_CHASSI`), etapas críticas (Mecânica, Caixa, Hidráulica, Lava-jato, Pintura), códigos padronizados de parada (`P01` a `P10`) e cálculo de prioridade baseado no lead time de referência.

## Metadados Operacionais
- **ID**: `consolidar-placas-pendentes-mecanica-drive-whatsapp-excel`
- **Complexidade**: `Medium`
- **Agentes Responsáveis**: `Gestor (DeepSeek V4 Pro), Pesquisador Sênior (DeepSeek R1), Engenheiro (Kimi K3), Analista (Cerebras)`
- **Modo de Execução**: `python` (`run.py`)
- **Status de Verificação**: ✅ Verificada e Homologada
- **Data de Homologação**: 2026-11-09
- **Regras Normativas**: POP-PCP-001 Rev 02 (COPAC / SGQ ISO 9001:2015)

## Padrões de Ativação
- `placas pendentes mecanica`
- `lista excel drive whatsapp`
- `consolidar planilhas drive`
- `relatorio placas pendentes`
- `placas falta mecanica`
- `status kanban copac`
- `cruzamento placas pop pcp`

## Comando de Verificação
```powershell
python run.py --check
```

## Evidência da Verificação
```
OK
Código de saída: 0
Verificação aprovada sem pendências.
```

## Instruções de Uso
```powershell
# Execução padrão gerando Placas_Pendentes_Mecanica.csv
python run.py

# Filtragem de placa ou chassi específico
python run.py --search "CX-2797"

# Personalização do nome do arquivo
python run.py --out "Relatorio_Kanban_Turno.csv"
```

---

## Conhecimento Incorporado: POP-PCP-001 Rev 02 (COPAC)

1. **Objetivo Operacional**: Acompanhamento de placa/chassi em todas as etapas fabris para garantia de rastreabilidade (ISO 9001:2015 Requisito 8.5.2) e cálculo de lead time.
2. **Separação de Fluxos**:
   - **NOVO**: Fabricação do zero a partir de chassi 0 km ou chassi de cliente.
   - **REFORMA**: Recuperação de frota LOPAC, reforma da caixa, encarroçamento, hidráulica/elétrica paralela dos cilindros, lava-jato final sem estética e pintura final da caixa.
   - **SÓ CHASSI**: Veículos que saem sem reforma de implemento.
3. **Lead Times de Referência**:
   - Mecânica: 15 dias
   - Hidráulica: 15 dias
   - Elétrica do Chassi: 15 dias
   - Elétrica do Implemento: 15 dias
   - Entre-eixo: 10 dias
   - Chaparia: 20 dias
   - Pintura: 5 dias
   - Lava-jato: 2 dias
4. **Matriz de Códigos de Parada (Anexo A)**:
   - `P01`: Falta de peça ou material
   - `P02`: Aguardando chassi
   - `P03`: Aguardando caixa
   - `P04`: Aguardando definição comercial/destino
   - `P05`: Aguardando outro setor (lava-jato, cilindros)
   - `P06`: Equipamento ou ferramenta em manutenção
   - `P07`: Mão de obra ausente ou realocada
   - `P08`: Retrabalho / Bloqueio de Qualidade
   - `P09`: Aguardando fornecedor externo (terceiro eixo, cilindros, bombas)
   - `P10`: Aguardando cliente, pagamento ou documento
