# Verificação de Planilhas conforme POP-PCP-001 Rev. 02

## Descrição
Valida planilhas de apontamento e consolidação de placas e caixas contra o fluxo de microprocessos NOVO e REFORMA do POP-PCP-001 Rev. 02. Confere colunas obrigatórias, valores permitidos de tipo de saída, microfase, etapas, donos e aplica as regras gerais do POP, como sem OS não começa e saiu sem destino não sai.

## Metadados Operacionais
- **ID**: `verificacao-planilhas-pop-pcp-001`
- **Complexidade**: `Medium`
- **Agentes Responsáveis**: `Manager, Assistant, Technician, Analyst`
- **Modo de Execução**: `powershell` (run.ps1)
- **Status de Verificação**: ⚠️ Pendente
- **Data de Criação**: 2026-11-10 13:29:48 UTC
- **Versão**: 1

## Padrões de Ativação
- `verificar planilha`
- `validar planilha`
- `pop-pcp-001`
- `fluxo novo`
- `fluxo reforma`
- `microfase`
- `apontamento`
- `etapa placa`
- `checar apontamento`
- `validar processo copac`

## Comando de Verificação
```powershell
python run.py --check
```

## Evidência da Verificação
```
Falha no teste: Codigo de saida: 2

[STDERR]: C:\Users\Ailton Soares\AppData\Local\Programs\Python\Python312\python.exe: can't open file 'C:\\Users\\Ailton Soares\\Documents\\print window mbx\\PROJETO FRIDA\\Agente Frida\\skills\\verificacao-planilhas-pop-pcp-001\\run.py': [Errno 2] No such file or directory
```

## Instruções de Uso
Execute o arquivo `run.ps1` passando os parâmetros operacionais necessários.

<!-- frida:skill-gerada -->
