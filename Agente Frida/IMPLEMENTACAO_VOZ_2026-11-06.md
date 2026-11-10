# Reconhecimento de voz — implementação 1.3.0.6

O código aplica as correções e disponibiliza a avaliação do plano de 06/11/2026. Ainda não há evidência de ganho acústico com a voz do operador: ele informou que não possui gravações de referência. Homologação física, escolha de limiares/modelo vencedor e meta de redução de erros continuam pendentes.

## Alterações funcionais

- `FridaSpeechRecognition.cs` centraliza Groq Whisper, validação do áudio, decisões de qualidade, timeout e cancelamento. Comando e gravação da IDE usam esse fluxo.
- A transcrição preserva o texto literal. Não há substituição automática Hilton/Ailton, nem descarte de frases legítimas apenas por corresponderem a uma lista de palavras.
- Silero verifica presença de fala antes da API, inclusive na IDE. Sem detector disponível o fluxo informa falha; não supõe que silêncio seja fala.
- Retornos distinguem `Ok`, `NoSpeech`, `SpeakerRejected`, `LowQuality`, `ServiceError` e `Cancelled`. Falha/baixa qualidade não vira chamada “Frida” bem-sucedida.
- Indicadores são avaliados por segmento. Segmento suspeito provoca nova tentativa do **áudio inteiro**; não se apagam palavras que poderiam conter negação ou número. Metadados ausentes/inválidos ou texto divergente dos segmentos não autorizam um comando.
- `no_speech_prob` elevado sozinho não elimina texto com boa probabilidade após VAD local. A lógica segue o tratamento conjunto de silêncio e probabilidade do [decodificador de referência do Whisper](https://github.com/openai/whisper/blob/main/whisper/transcribe.py). Os limites numéricos anteriores de probabilidade/compressão foram mantidos até calibração.
- Turbo continua como principal. Uma resposta de baixa qualidade pode usar **uma** tentativa no Large v3, sem terceiro provedor para resolver qualidade. Erro de serviço inicial pode usar a reserva Gemini existente. Áudio sem fala ou locutor rejeitado não gera tentativa de qualidade.
- Timeout por requisição, cancelamento propagado, modelos explicitamente permitidos no gerenciador e modelo principal refletido no painel.
- Fala que atinge o limite de 32 segundos sem terminar é solicitada novamente, sem executar uma transcrição cortada.
- A filtragem de locutor conserva os offsets originais e margens de até 100 ms, limita margens nas fronteiras de voz rejeitada e mantém silêncio nas posições removidas. Não promete separar vozes sobrepostas.
- Métricas locais registram estado, motivo, modelo, tentativas, duração, latência e custo estimado; captura registra saturação e divergências entre Silero e energia. Esses arquivos não armazenam áudio, texto ou embeddings e têm limite de tamanho de 5 MiB antes de reiniciar o arquivo.

## Configuração reversível

As novas opções têm valores padrão para configurações antigas. Não é necessário substituir `config.json` por um exemplo. Os valores ativos por padrão são:

| Opção | Padrão | Uso |
|---|---|---|
| `stt_primary_model` | `whisper-large-v3-turbo` | Aceita também `whisper-large-v3` |
| `stt_quality_retry_enabled` | `true` | Uma tentativa em Large v3 após baixa qualidade do Turbo |
| `stt_vocabulary_enabled` | `false` | Habilitar somente após comparação com negativos |
| `stt_vocabulary` | Lista curta do projeto | Limite local conservador de 200 bytes UTF-8; acima disso o contexto é desabilitado |
| `stt_gemini_fallback_enabled` | `true` | Mantém a reserva de serviço existente; não usada para resolver baixa qualidade |
| `stt_timeout_seconds` | `20` | Faixa aceita: 3–60 segundos por chamada |
| `voice_end_silence_seconds` | `0.60` | Faixa aceita: 0,4–2 s; testar 0,8/1 s com voz real |
| `voice_vad_energy_gate` | `true` | Preserva a condição de energia enquanto se mede sua influência |
| `voice_metrics_enabled` | `true` | Metadados locais em `voice_metrics.jsonl` e `voice_capture_metrics.jsonl` |

A regra de chamada sem comando mantém seu tempo específico, independente da pausa configurável do comando. Limiares de locutor e o perfil biométrico não foram reduzidos nem substituídos. As métricas de custo usam a tabela Groq consultada em 06/11/2026, mínimo de 10 s por requisição concluída; são estimativas, não faturas. Custos da reserva Gemini não estão incluídos nessa estimativa.

## Testes e ferramentas

O avaliador é um programa separado sem `FridaBrain`, sem assinatura do evento de despacho e sem execução de comandos. Os testes HTTP usam um provedor simulado. O teste nativo usa somente silêncio em memória e carrega os modelos locais; isso valida integração, não precisão humana.

Comandos abaixo executados a partir da pasta `Agente Frida`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\build_voice_evaluation.ps1
.\FridaVoiceEvaluation.exe --tests
.\FridaVoiceEvaluation.exe --check-native
powershell -NoProfile -ExecutionPolicy Bypass -File tests\run_tests.ps1
```

Para o cálculo de erros, usar JiWER existente em ambiente separado, sem adicionar dependência ao aplicativo:

```powershell
python -m venv scratch\voice-eval-env
.\scratch\voice-eval-env\Scripts\python.exe -m pip install -r tests\requirements-voice-eval.txt
.\scratch\voice-eval-env\Scripts\python.exe -m unittest discover -s tests -p test_voice_evaluation.py -v
```

## Como coletar e comparar a voz real

O conjunto de 14 frases incluído é um roteiro inicial, não o corpus de 300 gravações nem uma homologação. Adicionar situações reais de TV, outras pessoas, fala simultânea e reprodução da Frida, com transcrição conferida e `operator_voice` correto. Não usar voz sintética como substituta da voz do operador.

1. Fechar a Frida durante a coleta para evitar duas capturas independentes. Executar o comando de gravação em um terminal. Cada Enter inicia oito segundos; o programa pede confirmação/correção do texto realmente falado. Os arquivos ficam apenas na pasta escolhida.

```powershell
.\FridaVoiceEvaluation.exe --record VoiceEvaluation tests\voice_eval_prompts.json calibration
```

2. Fazer uma sessão diferente para teste reservado, sem escolher configurações a partir dela. O gravador cria subpastas e manifestos por sessão:

```powershell
.\FridaVoiceEvaluation.exe --record VoiceEvaluation tests\voice_eval_prompts.json test
```

3. Comparar os quatro candidatos substituindo `SESSAO` pelo diretório criado. **`--live` envia esses áudios para a Groq e consome cota**, usando a chave existente sem imprimi-la. O avaliador não usa Gemini nem executa ações. A referência é o áudio original idêntico para cada candidato; a decisão de locutor é registrada separadamente.

```powershell
.\FridaVoiceEvaluation.exe --evaluate VoiceEvaluation\SESSAO\manifest.json VoiceEvaluation\resultados --live
```

4. Calcular WER, comandos exatamente iguais, termos críticos presentes, rejeições, erros de serviço, p95 e custo. O script usa o JSONL específico produzido no passo anterior; substituir `ARQUIVO` pelo nome retornado:

```powershell
.\scratch\voice-eval-env\Scripts\python.exe tests\score_voice_evaluation.py VoiceEvaluation\resultados\ARQUIVO.jsonl --output VoiceEvaluation\relatorio.json
```

Rejeições de voz do operador continuam no denominador. O script recusa IDs duplicados, referências inconsistentes e mistura da mesma sessão entre calibração/teste. Resultados com amostras diferentes por candidato são sinalizados como não pareados. Presença de termos críticos não prova intenção correta; revisar manualmente números e negações.

Esse comparativo mede STT em áudio original e o efeito separado do filtro de identidade. A captura ao vivo, taxa de falsas ativações, desempenho do áudio filtrado, limite de latência e segurança da execução precisam de homologação específica. O relatório sempre marca `pending_physical_validation`; não promove automaticamente um modelo nem certifica a meta de 20%.

## Liberação e itens pendentes

O pacote 1.3.0.6 usa o instalador reversível existente. Ele preserva configuração, perfil de voz e recursos `wake`. O manifesto registra hashes dos arquivos e das fontes. O backup produzido pelo instalador permite restaurar o executável anterior.

Pendências do plano, sem evidência suficiente para encerramento:

- Coletar e revisar os áudios reais; separar calibração e teste por sessão.
- Medir a referência anterior e a candidata, incluindo captura, ativação e áudio filtrado; o avaliador pronto cobre apenas o escopo descrito acima.
- Selecionar modelo, vocabulário, fim de fala e eventual ajuste de identidade pelos resultados; verificar a meta de redução de erros, comandos completos e latência.
- Homologar uso ao vivo e acompanhar falhas reais. Compilação/testes automatizados não substituem essa etapa.
- WebRTC APM, Azure e Porcupine continuam condicionais ao diagnóstico do corpus, conforme o plano; não foram adicionados sem evidência da necessidade.

Fontes dos componentes e comparação: [Groq](https://console.groq.com/docs/speech-to-text), [Silero](https://github.com/snakers4/silero-vad), [sherpa](https://k2-fsa.github.io/sherpa/onnx/speaker-identification/index.html), [JiWER](https://github.com/jitsi/jiwer).
