# Compilação C# Nativa no Windows 11

## Descrição
Procedimento e parâmetros para compilação nativa de executáveis C# no Windows 11 usando csc.exe do .NET Framework 4.0 (x64) com prevenção de bloqueios de arquivos em execução.

## Metadados Operacionais
- **ID**: `compilacao_csharp`
- **Complexidade**: `Medium`
- **Agentes Responsáveis**: `Technician`, `Analyst`
- **Modo de Execução**: `instructional`
- **Status de Verificação**: ✅ Comprovado e Aprovado
- **Data de Criação**: 2026-11-10 09:30:00 UTC
- **Versão**: 1

## Padrões de Ativação
- `compile o agente frida`
- `compilar o projeto em c#`
- `erro de compilacao csc.exe`
- `sintaxe c# suportada`

## Instruções Operacionais
1. Localização do Compilador:
   - Caminho: `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`.
   - Plataforma: `/platform:x64`.
   - Flags padrão: `/nologo /optimize+ /target:winexe`.
2. Restrições de Sintaxe (C# 5 / .NET Framework 4.0):
   - Proibido pattern matching `is Dictionary<string, object> dict` (usar `as Dictionary<string, object>` + `!= null`).
   - Proibido interpolação de strings `$"..."` (usar concatenação `+` ou `string.Format`).
   - Proibido local functions.
3. Prevenção de Bloqueio (File-Lock):
   - Se o processo `FridaAgent.exe` estiver em execução, compilar diretamente sobre `FridaAgent.exe` gera erro `CS0016`.
   - Para teste de integridade da compilação, use sempre: `set FRIDA_BUILD_OUTPUT=FridaAgent.test.exe && compilar_frida.bat && del /f /q FridaAgent.test.exe`.
