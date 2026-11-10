# Conversa e pesquisa no navegador — 1.3.0.9

O atendimento de 08/11, 13:44 (horario de Sao Paulo), recebeu o pedido de pesquisar filamento PP no Google Chrome e colocar os resultados na tela. A triagem substituiu a rota tecnica por pesquisa simples. O registro de execucao contem pesquisa web, mas nenhuma abertura de URL; a etapa solicitada na tela ficou pendente.

A rota agora preserva explicitamente a necessidade de ferramentas de navegador/tela. Uma resposta `research` do classificador nao pode remover essa necessidade. Os testes reproduzem o texto do pedido e variantes com `pesquisa`, `coloca` e `no meu Google Chrome`. A execucao continua usando o motor de ferramentas e seus recibos; mudar a rota nao comprova que uma pagina foi carregada.

Nos registros de 13:44:36, 13:44:39 e 13:44:40, o verificador aceitou controles prioritarios. Em seguida o estado passou a OFF. O processo permaneceu ativo, com heartbeat saudavel, sem ciclo de inatividade. A frase reconhecida nao era registrada; nao e possivel concluir retrospectivamente se cada controle correspondia a uma ordem intencional.

O detector local agora aceita controles somente em hipoteses finais, sem palavras desconhecidas, dirigidos nominalmente a Frida. Exemplos: `Frida, pare` e `Frida, fique em off`. Palavras isoladas como `para` e prefixos incompletos nao podem cancelar o atendimento por esse caminho. Os controles confirmados passam a registrar a hipotese e o tipo de comando. A verificacao de locutor continua obrigatoria.

Parar uma resposta mantem a janela de conversa aberta; somente OFF explicito (ou o estado que ja estava OFF) permanece desligado. O prazo normal de inatividade nao foi alterado, pois nao causou os encerramentos observados.

Validacao: testes de regressao da triagem com resposta simulada incorreta e testes do detector para hipoteses parciais, palavras isoladas, desconhecidas e comandos explicitos. A validacao automatica nao substitui uma nova conversa no microfone nem a verificacao visual do Chrome.

Evidencias e backup dos fontes: `scratch/conversation-fix-20261108/`. Pacote com backup de instalacao: `scratch/release-conversation-1.3.0.9/`.

Resultado final: 196 testes C# aprovados; 79 testes Python descobertos, 74 aprovados e 5 ignorados. Versao 1.3.0.9 instalada com backup, mantendo configuracao e perfil de voz byte a byte. Inicializacao do microfone, VAD e detector local conferida nos logs. A conversa fisica e a abertura visual dos resultados pelo novo fluxo ainda precisam de validacao do operador.
