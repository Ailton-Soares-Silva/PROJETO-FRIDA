# Checkup de voz — 1.3.0.8

## Problemas confirmados

- O audio de treinamento alimentava o reconhecedor de comandos. A frase de cadastro `Frida, pare` podia cancelar a propria sessao. O cadastro agora tem prioridade na captura e os callbacks de comandos o ignoram.
- O perfil carregado usava limite 0,50. Em uma comparacao local com dez WAVs do cache de TTS, o executavel instalado 1.3.0.6 aceitou dois como voz do operador (scores 0,516 e 0,533). O candidato recusou os dez. Esse conjunto e pequeno e nao mede a taxa de erro com pessoas reais.
- A verificacao anterior podia aceitar a media de varias vozes, ignorar trechos curtos e deixar de verificar mudancas dentro de um unico segmento continuo de VAD.
- Nao havia medicao de ruido no cadastro; o teste de outra voz podia declarar sucesso ouvindo apenas silencio e usava tolerancia diferente da configuracao real.
- A transicao automatica entre frases, o cancelamento e a troca de microfone permitiam estados confusos ou frases coletadas em dispositivos diferentes.

## Alteracoes

1. Cadastro manual de cinco frases. Cada gravacao tem um segundo de preparacao silenciosa e sete segundos de leitura. O indicador `LEIA AGORA` delimita a fase correta. Nao inicia outra frase automaticamente.
2. Verificacao local de saturacao, intensidade, relacao voz/ruido, fala durante a preparacao, fala cortada e consistencia entre trechos da frase. Limites iniciais de voz/ruido: 12 dB no cadastro e 6 dB na verificacao, quando ha silencio suficiente para estimar o ambiente.
3. Perfis validos existentes sao preservados em disco; o limiar e recalibrado em memoria a partir da consistencia leave-one-out. Piso 0,58; teto da calibracao 0,72. Dados invalidos e cadastros inconsistentes nao liberam a escuta.
4. VAD da analise offline com limiar 0,60. Verificacao do locutor em janelas de 1,6 s, com passo de 0,8 s, incluindo o final de segmentos longos. Trechos isolados curtos ou identidade inconsistente impedem a liberacao do comando inteiro.
5. O audio aprovado preserva a linha do tempo e todas as palavras; somente intervalos sem fala sao silenciados. Nao remove palavras de outra pessoa para construir um comando parcial.
6. Chamadas inconclusivas e controles durante a TTS nao contornam mais a verificacao. `Oi Frida` ou `Frida, pare` fornecem mais evidencia que uma palavra muito curta; F4 continua disponivel.
7. Cancelar, reiniciar audio ou trocar o microfone descarta somente o cadastro em andamento. O perfil salvo continua preservado ate que as cinco novas frases sejam aprovadas.
8. Os testes usam a tolerancia configurada. Silencio ou um resultado inconclusivo nao comprovam rejeicao de outra pessoa. Relatorios locais incluem duracao, decisao e qualidade, sem salvar a gravacao do treinamento.
9. `FridaAgent.exe --voice-training` abre a tela de cadastro depois da inicializacao do audio.

## Validacao executada

- 196 testes C# aprovados, incluindo 29 testes de voz.
- Suite Python: 79 descobertos, 74 aprovados, 5 ignorados.
- Modelos Silero e WeSpeaker carregados localmente, sem API.
- Cinco entradas sinteticas negativas recusadas: silencio, ventilador tonal, ruido branco, cliques e sinal saturado. Todas tambem foram recusadas para cadastro.
- Dez trechos reais do cache de TTS convertidos localmente para PCM mono 16 kHz: 2 aceites indevidos no executavel instalado anterior; 0 no novo.
- Compilacao x64, verificacao de integridade na instalacao, backup do executavel anterior e conferencia de preservacao byte a byte de `config.json` e `frida_speaker_profile.json`.
- Tela de treinamento inspecionada; perfil disponivel e medidor de microfone ativo.
- Validacao fisica apos a instalacao: `Testar minha voz` aprovado, com 2,774 s de fala, score 0,8063 e limiar 0,6382; todos os trechos aprovados. O primeiro `Testar TV/outra voz` recebeu zero segundos de fala detectavel e foi corretamente marcado como inconclusivo. Na repeticao, detectou 3,308 s de fala e recusou o audio por relacao voz/ruido de 4,0 dB. A barreira de qualidade funcionou; esse resultado nao mede a discriminacao biometrica de outra pessoa em audio limpo, pois foi recusado antes do calculo de similaridade.

Evidencias locais (ignoradas pelo Git): `scratch/voice-checkup-20261108/`. Pacote: `scratch/release-voice-1.3.0.8/`. O backup original 1.3.0.6 esta no subdiretorio `backup-20261108-162918-11d8e114/rollback.json` desse pacote.

Reproducao, na pasta `Agente Frida`:

```powershell
.\tests\build_voice_evaluation.ps1
.\FridaVoiceEvaluation.exe --tests
.\FridaVoiceEvaluation.exe --check-native
.\FridaVoiceEvaluation.exe --check-speaker-noise .\scratch\voice-checkup-20261108\negative-audio
```

`tests/SpeakerBaselineProbe.cs` permite conferir um executavel anterior por reflexao, sem iniciar a aplicacao, microfone ou chamadas de rede. Informe executavel, perfil e pasta de WAVs negativos. Execute o probe na pasta dos recursos nativos.

## Validacao fisica e limites

O aumento de rigor pode exigir frases mais completas e um microfone mais proximo. Uma frase real do operador foi aprovada apos a instalacao. Medir taxas de aceitacao e rejeicao ainda exige varias frases, outras pessoas e diferentes condicoes; um acerto nao demonstra desempenho geral.

Use `Testar minha voz` e `Testar TV/outra voz` antes de decidir refazer o cadastro. Se refizer, grave sozinho e em distancia normal. Nao ha separacao garantida de pessoas falando simultaneamente: o sistema rejeita os casos em que detecta inconsistencia, mas nao promete distinguir toda sobreposicao nem gravacoes da propria voz do operador.

Referencias primarias para o papel distinto dos componentes: [Silero VAD](https://github.com/snakers4/silero-vad) e [sherpa-onnx: identificacao de locutor com VAD](https://github.com/k2-fsa/sherpa-onnx/blob/master/python-api-examples/speaker-identification-with-vad.py). Os limiares desta implementacao sao escolhas locais e precisam de validacao no ambiente; nao sao uma garantia publicada pelos fornecedores.
