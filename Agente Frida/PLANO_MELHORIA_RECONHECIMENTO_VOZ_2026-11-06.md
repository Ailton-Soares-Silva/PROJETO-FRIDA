# Plano de melhoria do reconhecimento de voz da Frida

Análise realizada em 06/11/2026 sobre o código e os arquivos locais do projeto, com consulta à documentação oficial dos fornecedores.

Atualização posterior: as correções de software e ferramentas de avaliação estão descritas em [Implementação de voz 1.3.0.6](IMPLEMENTACAO_VOZ_2026-11-06.md). Este plano permanece como diagnóstico original; a homologação com voz real ainda está pendente.

**Recomendação:** aproveitar Vosk, Silero e WeSpeaker já integrados; corrigir perdas na captura e nas decisões de rejeição; comparar Whisper Turbo com Large v3 na Groq existente. Introduzir outro fornecedor apenas se a comparação com áudio real demonstrar benefício. O entregável desta etapa é este plano; nenhuma mudança no motor em execução foi aplicada.

Ferramentas consolidadas não garantem, por si, melhora neste microfone e nesta voz. A documentação confirma os recursos disponíveis; a homologação proposta abaixo determina se a integração melhora a Frida. Não foi realizado benchmark acústico nem chamada paga a APIs nesta análise.

## 1. O que existe hoje

| Etapa | Implementação encontrada | Decisão inicial |
|---|---|---|
| Captura | WinMM, PCM 16 kHz mono, microfone selecionável e pré-gravação de aproximadamente 1,4 s | Preservar; medir saturação, perdas de quadros e corte de palavras |
| Ativação | Vosk pequeno português, gramática restrita; WinRT como alternativa | Manter e medir ativações incorretas e chamadas perdidas |
| Detecção de fala | Silero VAD v5 via sherpa-onnx, com condição adicional de energia | Manter; verificar se o veto por energia elimina fala baixa |
| Identificação do operador | WeSpeaker ResNet34-LM via sherpa-onnx | Calibrar com exemplos positivos e negativos reais |
| Transcrição | `whisper-large-v3-turbo` na Groq, idioma `pt`, temperatura 0 e resposta detalhada | Usar como referência da comparação |
| Reserva | Gemini configurado no projeto, acionado em certos erros do caminho principal | Registrar separadamente; validar disponibilidade e fidelidade antes de depender dele |
| Eco | Bloqueios durante reprodução, margem de locutor e correlação de áudio implementada no projeto | Medir; avaliar WebRTC APM se eco for causa relevante |

O README principal descreve WinRT e MFCC, mas o código atual usa Vosk e identificação neural. O planejamento acima segue o código, e a documentação operacional deve ser atualizada na entrega.

Configuração local observada, sem expor credenciais: filtro de locutor habilitado, tolerância 1, multiplicador de energia 1,45, limiar mínimo 140 e dispositivo preferido 0. Os modelos e DLLs de Silero/WeSpeaker estão presentes. Presença dos arquivos não equivale a comprovação de funcionamento em todas as sessões.

## 2. Achados que justificam as prioridades

As referências abaixo usam linhas do estado do código inspecionado; podem mudar após outras edições.

| Evidência local | Implicação | Ação proposta |
|---|---|---|
| `FridaEconomyRouting.cs:165`: encerramento por 0,60 s de silêncio; `AudioVoiceEngine.cs:1177` usa essa regra | Pausas naturais podem encerrar o comando. O tempo percebido também depende do atraso do VAD | Comparar 0,60/0,80/1,00 s, medindo cortes e latência; escolher pelo resultado |
| `AudioVoiceEngine.cs:1092`: fala depende de Silero **e** de energia/pico | Fala baixa pode ser descartada apesar da detecção neural | Instrumentar as duas decisões e testar a necessidade do segundo veto |
| `AudioVoiceEngine.cs:1420`: médias de ausência de fala/probabilidade ou um segmento com compressão elevada descartam a resposta inteira | Um trecho problemático pode eliminar conteúdo aproveitável; o efeito real ainda precisa ser medido | Avaliar segmentos individualmente, sem executar frases incompletas com sentido alterado |
| `AudioVoiceEngine.cs:1472`: resposta vazia da Groq é definitiva; somente `null` segue para reserva | Silêncio, rejeição de qualidade e falha técnica não têm tratamento suficientemente distinto | Retornar estados explícitos e permitir uma segunda tentativa apenas quando há fala válida e qualidade insuficiente |
| `AudioVoiceEngine.cs:1342`: substituição global de “Hilton” por “Ailton” | Uma transcrição correta de Hilton é alterada deterministicamente | Remover a troca global na implementação e cobrir os dois nomes em regressão |
| `AudioVoiceEngine.cs:1369`: contexto de vocabulário desabilitado, com comentário sobre reprodução do prompt no silêncio | Nomes técnicos não recebem contexto, mas há risco local já registrado | Testar vocabulário curto só após presença de fala, incluindo silêncio nos testes; ativar apenas com ganho comprovado |
| `SpeakerVoiceProfile.cs:264`: falas abaixo de 0,5 s são insuficientes; `AudioVoiceEngine.cs:1280` rejeita comando inconclusivo | Respostas breves podem parar antes do STT | Medir “sim”, “não” e comandos curtos; tratar inconclusivo com pedido de repetição, preservando a política de locutor |
| `SpeakerVoiceProfile.cs:274–307`: exclusão de segmentos e criação de WAV filtrado quando há remoções | Pode perder contexto ou unir trechos; não separa duas vozes sobrepostas no mesmo segmento | Preservar tempos/margens dos segmentos; comparar original e filtrado em avaliação controlada |
| `LocalWakeDetector.cs:63–80,111`: variantes “frita/ferida” e acionamento a partir de resultados parciais ou finais | Pode melhorar cobertura, mas também aceitar palavras parecidas | Comparar cada variante com negativos reais e verificar palavras presentes no vocabulário |
| `AudioVoiceEngine.cs:2553`: detecção própria de eco por correlação | Detecta sem necessariamente cancelar o eco | Se necessário, usar cancelador consolidado na etapa de áudio |

Nos últimos 2.500 registros do arquivo `frida_boot.log`, as verificações de locutor encontradas abrangem 03/11/2026 23:07:02 a 06/11/2026 12:11:29, conforme os timestamps gravados:

| Tipo de verificação | Total | Aceitas | Descartadas |
|---|---:|---:|---:|
| Chamada | 38 | 37 | 1 |
| Comando | 161 | 139 | 22 |
| Controle prioritário | 487 | 359 | 128 |
| Total | 686 | 535 | 151 |

Isso mostra onde há descartes, **não uma taxa de erro**. Os registros não têm rótulo humano que permita saber quais rejeições foram corretas, podem incluir versões/sessões distintas e não são necessariamente tentativas independentes. Nenhuma precisão percentual da Frida pode ser inferida desses totais.

## 3. Ferramentas selecionadas e condições de uso

1. **Silero VAD + sherpa-onnx + WeSpeaker: preservar a integração existente.** Silero é um detector de atividade de fala; sherpa disponibiliza identificação de locutor com modelos prontos. Detecção de fala e identidade são decisões distintas. A presença de voz de TV também pode ser detectada como fala. Fontes: [Silero](https://github.com/snakers4/silero-vad) e [identificação de locutor no sherpa](https://k2-fsa.github.io/sherpa/onnx/speaker-identification/index.html).

2. **Groq Whisper Large v3: primeiro candidato de transcrição.** O fornecedor disponibiliza Turbo e Large v3 e recomenda o segundo para aplicações sensíveis a erros. A API aceita contexto curto, até 224 tokens, e metadados por segmento. Usar esses recursos em comparação controlada, sem presumir que os indicadores sejam probabilidades calibradas de acerto. Fonte: [Groq Speech to Text](https://console.groq.com/docs/speech-to-text).

3. **JiWER: avaliação de transcrição.** Usar a biblioteca existente para WER/CER; manter a mesma normalização entre candidatos. Complementar com acerto de nomes, números, negações e comando completo. Fonte: [JiWER](https://github.com/jitsi/jiwer).

4. **WebRTC Audio Processing Module: candidato para eco e ruído.** O componente oferece cancelamento de eco, supressão de ruído e controle de ganho e pode ser usado separadamente do restante do WebRTC. Integrar somente se os testes de bancada justificarem. Fonte: [WebRTC APM](https://webrtc.googlesource.com/src/+/refs/heads/main/modules/audio_processing/g3doc/audio_processing_module.md).

5. **Azure AI Speech: alternativa de segunda etapa.** Tem suporte a português brasileiro e listas de frases para nomes e termos de domínio, sem treinamento de modelo. Testar se as opções existentes não atingirem as metas; requer integração, conta e orçamento próprios. Fontes: [idiomas do Azure Speech](https://learn.microsoft.com/pt-br/azure/ai-services/speech-service/language-support) e [listas de frases](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/improve-accuracy-phrase-list).

6. **Vosk primeiro; Porcupine somente se a ativação continuar falhando.** Vosk admite vocabulário restrito nos modelos compatíveis. Porcupine oferece palavra de ativação personalizada em português para Windows. Comparar o modelo gerado de “Frida” com Vosk antes de contratar/adotar; verificar licença e compatibilidade com o runtime do projeto. Fontes: [Vosk](https://alphacephei.com/vosk/adaptation), [Porcupine Model API](https://picovoice.ai/docs/model-api/porcupine/) e [integração .NET](https://picovoice.ai/docs/quick-start/porcupine-dotnet/).

A proposta não inclui treinamento de ASR próprio. A implementação necessária é integração, configuração e avaliação dos componentes existentes. Modelo personalizado de ativação, se necessário, seria gerado pela ferramenta do fornecedor.

## 4. Sequência de implementação

Estimativas de esforço para uma pessoa com acesso ao projeto; começam após disponibilidade das gravações. São estimativas de planejamento, não prazo já assumido.

| Ordem | Trabalho e entrega | Esforço estimado | Dependência |
|---|---|---|---|
| P0 | Corpus de avaliação, execução repetível e relatório de referência | 1–2 dias + coleta | Áudios reais e transcrições conferidas |
| P1 | Estados explícitos do STT, motivos de rejeição, correção de nomes e tratamento de falha | 1–2 dias | P0 |
| P1 | Ajuste de fim de fala, energia, locutor e preservação de segmentos | 2–3 dias | P0; comparar uma mudança por vez |
| P1 | Comparação Turbo/Large v3 e vocabulário; seleção pela qualidade/custo | 1–2 dias | P0 e caminho de captura estabilizado |
| P2 | Eco/ruído com WebRTC APM, se o corpus mostrar necessidade | 3–5 dias | Referência PCM de reprodução e integração nativa x64 |
| P2 | Azure ou Porcupine, apenas para lacuna comprovada | Estimar após P1 | Acesso ao fornecedor e resultado comparativo |
| Liberação | Teste ao vivo, pacote reversível e acompanhamento inicial | 1–2 dias | Critérios de aceite satisfeitos |

**P0 — medir antes de escolher configurações.** Montar aproximadamente 300 gravações: 200 para ajustar parâmetros e 100 reservadas para avaliação final, separadas por sessão de gravação. Incluir voz normal/baixa, microfone próximo/distante, ventilador, teclado, pausas, nomes, números, frases técnicas e respostas curtas. Acrescentar conjunto negativo separado de silêncio, TV, outras pessoas e fala sintetizada da Frida, além de pelo menos duas horas de ambiente para ativação. Ampliar a amostra se os resultados forem próximos ou houver poucos exemplos por condição.

Guardar por tentativa: identificador, versão, dispositivo, duração, presença de fala, decisão do locutor, motivo do descarte, modelo STT, latência, custo estimado e resultado final. Gravações de diagnóstico devem ser explícitas e locais, fora do Git; não habilitar gravação contínua do ambiente como efeito colateral.

**P1 — captura e identidade.** Manter o pré-roll existente e verificar a primeira/última palavra. Avaliar a regra de 0,60 s junto ao atraso do Silero. Registrar decisões divergentes entre VAD e energia. Usar gravações do operador e de outras vozes para calibrar identidade; reduzir rejeições sem simplesmente baixar o limiar. O cadastro atual é uma referência de identidade, não treinamento do transcritor.

Na filtragem, preservar os índices originais e margens de contexto dos trechos retidos, sem recuperar segmentos identificados como outra pessoa. Hoje `ExtractSegments` retorna amostras e não conserva seus offsets para o chamador; essa mudança exige testes de fronteira. Vozes simultâneas devem ser classificadas como caso difícil, não anunciadas como voz perfeitamente isolada.

**P1 — transcrição.** Comparar quatro combinações nos mesmos áudios: Turbo atual, Turbo com vocabulário, Large v3 sem vocabulário e Large v3 com vocabulário. Exemplo inicial curto de vocabulário: “Frida, Ailton Soares, MBX, SolidWorks, WhatsApp”. Selecionar somente termos relevantes à tarefa, incluir “Hilton” como caso legítimo e testar se o contexto produz palavras em silêncio.

Transformar retorno textual ambíguo em resultado com estados `ok`, `sem_fala`, `locutor_rejeitado`, `baixa_qualidade`, `erro_servico` e `cancelado`. Silêncio e locutor rejeitado não devem gerar tentativas extras de STT. Em baixa qualidade com fala válida, testar uma única segunda transcrição; se continuar ambíguo, solicitar repetição. Erros de serviço precisam de política distinta, com cancelamento e limite de tentativas.

Não tratar a concordância entre Turbo e Large v3 como confirmação independente: são modelos relacionados. Se o Large v3 vencer de forma consistente, considerar usá-lo diretamente, evitando uma cascata que aumenta latência. O método `CallGeminiTranscribeAsync` deve ser renomeado para refletir o roteamento, e a política em `ModelSelection.IsAllowed` precisa admitir explicitamente os modelos homologados; alterar apenas a string do modelo não basta.

**P2 — áudio e ativação.** Antes de adicionar processamento, comparar o microfone atual com headset disponível, fixar dispositivo e verificar saturação. Para WebRTC, enviar áudio de reprodução como referência do cancelador e adaptar os quadros/formatos exigidos pela API; o buffer atual de detecção de eco não substitui essa integração. Comparar processamento ligado/desligado: redução de ruído agressiva pode prejudicar a fala. Revisar a gramática Vosk e palavras desconhecidas antes de trocar o detector.

## 5. Critérios de aceite

Metas iniciais propostas, a confirmar após a referência. Não são ganhos já obtidos.

| Medida | Critério proposto |
|---|---|
| Erros de palavras | Buscar redução relativa de pelo menos 20% no WER total, sem regressão material nas condições críticas; exemplo: 15% para 12% |
| Entendimento completo | Pelo menos 95% dos comandos no conjunto silencioso com intenção e campos corretos; incluir toda tentativa rejeitada no denominador |
| Nomes/números/negações | Nenhuma corrupção nos casos críticos de regressão; informar resultado exato e tamanho da amostra |
| Silêncio e outra voz | Nenhum comando executado no conjunto negativo; relatar também ativações e transcrições indevidas |
| Locutor | Reduzir falsas rejeições sem aumentar falsas aceitações no conjunto reservado |
| Cortes de fala | Nenhum corte nos casos específicos de pausas e primeira/última palavra |
| Ativação | Buscar pelo menos 95% de chamadas detectadas e no máximo 1 ativação falsa/hora no ambiente de teste; ampliar duração para afirmar estabilidade |
| Latência | Relatar mediana e p95 do fim da fala até o texto; orçamento inicial de acréscimo de até 500 ms no caminho normal |
| Custo | Relatar custo por 1.000 comandos e percentual de segunda tentativa |

Medir WER tanto no áudio enviado ao transcritor quanto no resultado ponta a ponta. Assim, rejeições prévias não desaparecem da avaliação. Normalizar caixa e pontuação consistentemente; avaliar separadamente o valor exato de números, nomes e negações. Reportar contagens e incerteza: amostra pequena não comprova ausência de falhas raras.

Testes de software devem cobrir silêncio, baixa qualidade, resposta sem metadados, erro HTTP, cancelamento, “Hilton/Ailton”, fala curta, segmento misto e comando com negação. Testes acústicos precisam usar gravações reais; texto simulado e testes unitários não demonstram precisão do reconhecimento.

## 6. Custo e liberação

Na documentação consultada, a Groq informa US$ 0,04/h para Turbo e US$ 0,111/h para Large v3, com mínimo faturável de 10 segundos por requisição. Para 1.000 comandos de até 10 segundos, isso corresponde aproximadamente a US$ 0,11 ou US$ 0,31; uma cascata Turbo + Large v3 em 20% dos comandos fica em cerca de US$ 0,17. São cálculos de tabela, sem impostos, câmbio ou franquias. Fonte: [preços e faturamento de STT na Groq](https://console.groq.com/docs/speech-to-text).

Fórmula: somar `max(duração_em_segundos, 10) / 3600 × preço_hora` de cada requisição efetivamente feita. Comandos maiores e novas tentativas aumentam o total. Confirmar limites da conta no início do teste.

Liberar por configuração reversível, uma alteração por vez, mantendo pacote anterior. Executar primeiro avaliação sem despacho de ações: transcrição experimental não deve acionar ferramentas. Depois, homologar no microfone de uso e acompanhar falhas reais. Reverter a alteração se houver aumento de comandos incorretos, rejeições ou latência acima do orçamento.

**Entrega da implementação futura:** relatório comparativo reproduzível, modelo/configuração vencedores, testes de regressão, documentação atualizada e pacote reversível. A implementação e sua homologação permanecem como próximas etapas; este documento conclui o diagnóstico e o plano solicitados.
