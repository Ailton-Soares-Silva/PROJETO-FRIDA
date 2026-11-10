# Chamada e parada — 1.3.0.10

Regressao constatada: a regra 1.3.0.9 rejeitava `pare` e `para` sem o nome Frida, inclusive durante a reproducao. A chamada de 13:51:00 tambem foi recusada por SNR de 4,3 dB antes de avaliar a identidade. O cadastro feito pelo operador elevou o limiar normal para 0,72; a penalidade de fala curta exigia ate 0,75.

Alteracoes:

- Paradas locais finais aceitam `pare`, `para`, `chega` e `cancela`. OFF continua exigindo nome e frase concluida. Negacoes e hipoteses com palavras desconhecidas continuam bloqueadas.
- Comandos compostos parciais precisam permanecer estaveis por 300 ms antes de acionar a verificacao. Chamadas tambem aguardam estabilidade, evitando consumir o primeiro fragmento do nome.
- Buffer de controles aumentado de aproximadamente 1,4 para 3 segundos para preservar a voz enquanto o reconhecedor espera o fim da frase.
- Chamada/parada usam verificacao neural propria, com limite 0,68, sem penalidade adicional de frase curta. Saturacao, intensidade minima, fala humana e identidade continuam exigidas. O SNR de intervalos ao redor da fala nao bloqueia essa verificacao, pois esses intervalos podem conter a propria TTS.
- Comandos de execucao continuam usando a verificacao completa de qualidade e consistencia dos segmentos. O cadastro foi preservado.
- Configuracao local: Whisper Large v3, vocabulario existente ativado, pausa final de 0,9 s. O arquivo anterior esta no backup local. Essa alteracao precisa de comparacao com a fala real para medir ganho de transcricao.

Validacao automatica: 197 testes C# aprovados, incluindo 30 de voz; 74 testes Python aprovados e 5 ignorados. Auditoria nativa com silencio, ventilador tonal, ruido branco, cliques, sinal saturado e dez WAVs do cache da Frida. Incluiu extratos de 1,4 s: limite experimental 0,62 aceitou dois trechos de TTS (0,627 e 0,624); o limite final 0,68 recusou todos os casos testados. Esses exemplos nao constituem garantia contra todo eco ou outra pessoa.

Pacote: `scratch/release-voice-1.3.0.10/`. Evidencias, fontes anteriores e backup de configuracao: `scratch/voice-usability-20261108/`. Teste fisico de chamada e interrupcao solicitado ao operador apos a instalacao.

## Seguimento: 1.3.0.11

A chamada fisica passou (score 0,865 em 0,7 s), mas o operador informou que os pedidos continuavam incompreendidos. Os registros contem fragmentos como `ate 20`; nao havia gravacao bruta guardada para comparar a frase exata. Tambem houve parada recusada com score 0,489 durante a reproducao.

Foram removidos dois caminhos que descartavam audio: silenciamento por VAD antes da transcricao e descarte do pre-roll durante os 350 ms de guarda apos TTS. Agora uma captura aprovada para identidade e enviada integralmente, e a guarda conserva o inicio da proxima fala. A guarda ainda impede disparo prematuro; audio durante a propria TTS continua fora da captura normal.

Parada sob mistura com TTS: dentro de 120 s de um comando com voz verificada, enquanto a Frida esta falando/processando, `pare`, `pare de falar`, `para de falar` ou parada nominal podem interromper mediante fala humana e rejeicao de eco correlacionado. Esse caminho NAO comprova novamente a identidade do locutor: outra pessoa proxima pode conseguir parar uma resposta nesse intervalo. Ele nao permite OFF nem nova execucao. `para` sozinho continua exigindo a verificacao normal nesse cenario. A concessao e limitada a cancelar a resposta atual.

32 testes de voz passaram, incluindo preservacao do pre-roll e limites dessa parada. Pacote `scratch/release-voice-1.3.0.11/`; teste fisico solicitado novamente. Os resultados anteriores de reconhecimento da chamada nao comprovam que a transcricao de comandos esteja corrigida.
