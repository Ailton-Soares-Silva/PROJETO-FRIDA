# Entrega funcional FRIDA — 4 de novembro de 2026

Versao **1.1.0.0** compilada e instalada em `FridaAgent.exe`.

SHA-256: `92ce86e2afc721ed94c4df8deb2c2bc8f5066a74aab4197d20fe2896cd8d098e`.

## Alteracoes entregues

- Classificacao compartilhada para explicacoes, consultas reais e acoes, respeitando comandos negados.
- Comandos nativos de desligamento/reinicio/cancelamento e terminal sem depender de LLM, com respostas derivadas dos recibos.
- Duracoes compostas e decimais, sem prazo presumido. Resposta ao esclarecimento retoma o pedido pendente por ate cinco minutos.
- Finalizacao compartilhada de texto/fala/memoria, verificacao de operacao/alvo e agregado de conclusao. Aceitacao pelo Windows continua identificada como aceitacao.
- Cache de operacoes por atendimento compartilhado entre provedores, incluindo falhas e resultados incertos. Limite de dois provedores na cadeia operacional.
- Busca incremental, cancelavel e limitada a dez segundos, priorizando o disco informado e correspondencia exata no nivel mais proximo. Ambiguidades apresentam caminhos numerados; a escolha retoma o caminho exato.
- Um unico adaptador do WhatsApp, com lock de perfil entre processos, leitura concorrente das saidas, JSON final unico, estados distintos e pareamento seguido de uma repeticao da consulta.
- Correcao do carregamento da pagina do WhatsApp com estrategia eager e verificacao do DOM mesmo apos timeout de navegacao.
- Cache SQLite com fechamento explicito das conexoes, evitando bloqueios de arquivo no Windows.
- Heartbeat atomico a cada 15 segundos, sinal da UI, diagnostico de falha, recuperacao de registros interrompidos e mutex sem encerramento indiscriminado de processos.
- Inatividade de 30 segundos preserva a reativacao; captura de audio com uma tentativa de recuperacao e encerramento coordenado da thread.
- Relatorios registram canal de entrada e pedido resolvido; heartbeat conta ciclos de inatividade para homologacao real.

## Validacao executada

**81 testes C# e 20 testes Python aprovados.** Incluem HTTP 429 simulado, entrada invalida/vazia, comandos negados, parametros ausentes, resultados parciais, alvo/operacao incorretos, cache de operacoes incertas, pareamento/repeticao e exclusao de perfil.

Os 30 atendimentos e os 10 ciclos dos testes automatizados usam adaptadores/estados simulados. Nao substituem 30 atendimentos reais por texto e voz.

Verificacoes reais:

- `G:\Meu Drive\PLB_INTERNO` e `G:\Meu Drive\JSA_INTERNO` localizados corretamente.
- Windows aceitou agendamento de desligamento para 7200 segundos e o cancelamento imediato em `finally`; ambos terminaram com codigo zero. Os recibos estao em `tests/results/windows-smoke.json`.
- Aplicacao iniciou com o microfone e os modelos locais; uma segunda instancia sinalizou a principal e encerrou com codigo zero, sem substituir o PID. Monitor de um minuto coletou 12 amostras sem falhas de processo, heartbeat ou UI; evidencia em `tests/results/desktop-smoke.json`.
- WhatsApp retornou `NEEDS_QR`. A janela de pareamento foi aberta, mas expirou sem autenticacao confirmada.

Evidencias: `tests/results/functional-validation.log`, `tests/results/release-manifest.json` e os relatorios `stability-*.json`.

## Homologacao ainda pendente

1. Parear o WhatsApp pelo comando **Frida, pareie o WhatsApp**, confirmar grupos reais e repetir a consulta depois de reiniciar a aplicacao. O script confirma o filtro de grupos; interfaces nao reconhecidas retornam `dom_changed` em vez de inventar uma lista.
2. Executar duas horas de observacao, com pelo menos 30 atendimentos reais incluindo texto e voz e dez ciclos de inatividade/reativacao. Conferir tambem os resultados funcionais observados.
3. A qualidade da escuta, reproducao da voz, janela real do terminal e recuperacao apos desconectar fisicamente o microfone ainda exigem verificacao interativa. Sem confirmacao de janela, a abertura do terminal e relatada como solicitacao enviada.

A homologacao integral nao esta marcada como concluida. O monitor curto verifica processo/heartbeat/UI; nao certifica os criterios de duas horas.

## Reproduzir e restaurar

Na pasta `Agente Frida`, execute:

```powershell
.\tests\run_tests.ps1 -DriveSmoke
# O teste abaixo agenda um desligamento para duas horas e cancela imediatamente:
.\tests\run_tests.ps1 -DriveSmoke -WindowsSmoke
.\tests\monitor_stability.ps1 -Minutes 120
```

Configuracoes opcionais em `config.json`: `whatsapp_python`, `whatsapp_profile` e `whatsapp_chrome`. Valores vazios usam descoberta local e o perfil existente da aplicacao.

Para recuperar explicitamente uma instancia sem resposta, obtenha PID, caminho e horario de inicio do processo e use `FridaAgent.exe --recover <PID> <caminho-do-executavel> <inicio-UTC-ISO8601>`. A recuperacao confere os tres identificadores antes de encerrar o processo indicado.

O baseline preserva o codigo e o executavel anteriores, incluindo as alteracoes locais que ja existiam. Para restaurar, feche a FRIDA pelo menu **Sair** da bandeja e execute:

```powershell
.\tests\restore_previous.ps1 -BaselinePath .\scratch\baseline-functional-20261104-102535
```

Configuracao, credenciais, perfis e dados atuais ficam preservados. Auditoria de etica/LGPD permanece fora desta etapa.
