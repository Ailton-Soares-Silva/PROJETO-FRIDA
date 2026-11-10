# Extração Segura de Documentos Locais

## Descrição
Procedimento operacional para leitura e extração limpa de conteúdo de arquivos PDF, planilhas Excel (.xlsx/.csv) e documentos Word (.docx) locais sem travar a interface e sem abrir processos externos na tela do operador.

## Metadados Operacionais
- **ID**: `extracao_documentos`
- **Complexidade**: `Low`
- **Agentes Responsáveis**: `Technician`, `Analyst`
- **Modo de Execução**: `instructional`
- **Status de Verificação**: ✅ Comprovado e Aprovado
- **Data de Criação**: 2026-11-10 09:30:00 UTC
- **Versão**: 1

## Padrões de Ativação
- `leia o arquivo pdf`
- `extraia os dados da planilha`
- `leia o documento word`
- `conteudo deste relatorio`

## Instruções Operacionais
1. Para inspecionar ou ler arquivos locais de texto ou formatos compostos:
   - Use SEMPRE a ferramenta `manage_files` com `operation='read'`.
   - O extrator nativo converte internamente PDFs, DOCX e XLSX para texto plano legível e estruturado.
2. NUNCA utilize `Start-Process` para abrir visualizadores de PDF, Excel ou Word na tela com o objetivo de capturar a tela via `inspect_screen`.
3. Se precisar processar planilhas grandes ou filtros tabulares complexos, use scripts Python (`python.exe`) com argumentos via CLI gravando o resultado em arquivo de saída ou imprimindo em stdout.
