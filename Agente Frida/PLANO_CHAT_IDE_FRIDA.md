# Chat IDE Frida — plano revisado

O Chat IDE será a janela de trabalho da Frida no Windows. O operador poderá começar um pedido por voz, continuar pelo chat, abrir o script criado e acompanhar a execução no computador ou na VPS escolhida. A janela abre no chat e mostra os controles de desenvolvimento ao selecionar um projeto.

## Ponto de partida

O executável conferido é o **1.2.1.0**. O projeto já possui o orbe, atendimento por voz, memória compartilhada, relatórios por atendimento, ferramentas para arquivos, execução local de PowerShell e painel Zelda. O copiloto do WhatsApp usa um worker Python com protocolo JSONL, perfil próprio, cache SQLite, pesquisa ao vivo, progresso e referências. O pareamento atual abre um navegador visível. O executor local já lê saída e erros em paralelo e consolida o recibo no fim. A integração SSH e o editor de projetos entram nesta entrega.

## Janela e atendimento

- Usar WinForms, a paleta azul e ciano da Frida e Segoe UI. A janela começa em aproximadamente 1040 × 720, ajustada à área útil da tela. A lateral recolhível agrupa **Chat, Histórico e WhatsApp**, **Projetos, Código, Terminal e Arquivos**, **Reuniões, Planos e Zelda Manager Group**. Exibir projeto e destino ativos nas telas de desenvolvimento.
- Trocar os três itens do print por **Abrir Chat IDE Frida** no menu do ícone da bandeja. **Ctrl+Alt+F** abre ou traz o chat para frente. **Ctrl+Alt+L** abre o último atendimento no histórico. **F4** chama ou interrompe o atendimento ao vivo. “Ficar em OFF” passa aos controles de voz do chat. Fechar a janela a recolhe e permite acompanhar a mesma tarefa ao reabrir.
- Chat e voz usam o mesmo cérebro e contexto. Identificar a origem de cada turno. No chat, apresentar progresso, resposta e fontes na janela, com reprodução de voz por clique. Reservar voz automática, orbe e avisos externos para o canal ao vivo. Se houver atendimento ativo, mostrar o estado e conservar o rascunho.
- Gravar áudio pelo microfone da Frida, com contador de até cinco minutos, parar, cancelar, ouvir, revisar a transcrição e enviar. Ao concluir ou cancelar a gravação, devolver a captura ao estado anterior. Guardar somente o áudio enviado. Oferecer controles nomeados para leitor de tela, foco visível e operação por teclado.

## WhatsApp Web

1. Ao abrir a seção ou acionar **Verificar conexão**, consultar a sessão real do perfil utilizado pela Frida. Criar uma consulta de status que apenas observa o estado, pois o fluxo atual de consultas pode iniciar pareamento automaticamente. Mostrar última verificação e distinguir **Verificando**, **Conectado**, **Aguardando QR**, **Leitor ocupado** e **Não foi possível verificar**. Distinguir a disponibilidade do cache da conexão atual.
2. Quando o perfil pedir autenticação, oferecer **Conectar com QR**. O mesmo worker abre o pareamento em segundo plano, captura apenas a imagem do QR exibido pelo WhatsApp e a transmite por um evento JSONL associado ao pedido. Renderizar a imagem na própria seção, com instrução curta para escanear pelo menu de aparelhos conectados do celular.
3. Substituir o QR quando a página o renovar. Oferecer cancelar e tentar novamente. Ao observar a interface autenticada no mesmo perfil, retirar a imagem e mostrar **Conectado**. Uma expiração, queda de rede ou mudança na página deve resultar no estado observado e numa ação de recuperação.
4. Serializar o acesso ao navegador com o bloqueio já existente. Uma verificação do painel respeita pesquisas em andamento. Se uma pesquisa iniciada no chat precisar de pareamento, abrir a seção, aproveitar a mesma tentativa e retomar a consulta uma vez após autenticar. Encaminhar progresso, resultados parciais e referências de volta ao atendimento de origem. O QR é temporário e fica exclusivamente em memória, fora de logs, histórico, terminal e contexto dos modelos.
5. As perguntas sobre mensagens continuam usando o copiloto analítico existente. No chat, apresentar a síntese com referências, período consultado e cobertura informada pelo leitor. Preservar a escolha entre grupos homônimos e a retomada de uma pesquisa. Na regressão, incluir uma pergunta de quantidade por período, uma escolha de conversa e uma busca parcial.

## Projetos, Código e Terminal

| Seção | Experiência do operador |
|---|---|
| **Projetos** | **Criar projeto** escolhe nome, ambiente e diretório pai e cria a raiz. **Abrir pasta como projeto** associa uma pasta existente. Para VPS, escolher uma conexão SSH e um diretório remoto. Mostrar recentes, raiz e disponibilidade. Retirar um projeto da lista preserva seus arquivos. |
| **Código** | Árvore de diretórios e editor com abas, números de linha, indicação de alterações e busca. O pedido “Frida, crie um script neste projeto” usa a raiz selecionada. Mostrar a alteração proposta, permitir comparar com o arquivo existente, editar, salvar e executar. Conflito com edição manual ou externa abre comparação antes da gravação. |
| **Terminal** | Exibir as ações de desenvolvimento da Frida vindas do chat, da voz, do editor ou da equipe Zelda. Cada execução mostra projeto, computador ou VPS, diretório, comando, saída e erros conforme chegam, duração e resultado. Ações diretas sobre arquivos aparecem como eventos clicáveis, por exemplo **Arquivo criado · scripts/diagnostico.py**. **Parar execução** atua na tarefa selecionada. |

- Associar cada projeto a uma raiz e a um ambiente. No início do pedido, fixar projeto, destino e diretório naquele atendimento. Mudar a seleção durante uma execução só afeta pedidos posteriores. Trabalho fora de projeto continua identificável pelo ambiente e caminho usados.
- Instrumentar os adaptadores reais de arquivos e processos em um ponto comum. Publicar eventos com identificadores de atendimento, projeto e execução. O painel acompanha comandos enquanto rodam e o histórico guarda um registro de tamanho limitado com o resultado final. Associar leitura, gravação, compilação e testes ao arquivo correspondente sempre que o adaptador conhecer o caminho. Separar saída do processo de eventos de arquivo. Reservar “verificado” para uma checagem efetivamente executada.
- **Computador** usa o PowerShell existente e o diretório do projeto. **VPS** usa os clientes OpenSSH e SFTP disponíveis no Windows. Cadastrar um alias SSH, conferir a identidade do servidor na primeira conexão e usar a identidade ou o agente SSH do operador. Testar a conexão e o acesso à raiz antes de habilitar a execução. Selecionar o interpretador adequado ao destino e à extensão do script. Se faltar, informar qual dependência precisa ser instalada.
- Para destino remoto, ler e salvar por SFTP e executar por SSH no diretório indicado. Gravar alterações em arquivo temporário na mesma pasta, conferir a versão anterior e publicar a alteração. Ao perder conexão durante uma gravação ou execução, marcar **resultado a confirmar**, reconectar ao mesmo destino e verificar antes de permitir repetição. Ao interromper trabalho remoto, pedir encerramento do processo da Frida no servidor e confirmar o resultado.
- O terminal acompanha sessões e ferramentas iniciadas pela Frida. Um programa externo aberto em janela própria recebe um evento de abertura, com o nível de confirmação disponível. Aplicar ocultação de credenciais antes de publicar ou persistir comandos e saídas.

## Arquivos, histórico e equipe

- **Arquivos** apresenta itens criados e pastas acessadas conforme os recibos, com busca, favoritos, abertura, cópia de caminho, criação de pastas, renomeação e movimentação. No Windows, usar a Lixeira. Para remoção remota dentro do projeto, mover para uma pasta de recuperação no mesmo servidor e registrar o caminho de restauração. Tratar colisões, falta de permissão e caminhos indisponíveis.
- **Histórico** reúne pedido, resposta, fontes e execuções relacionadas, com pesquisa, filtros e paginação. Do atendimento, abrir o arquivo, a execução do terminal ou a reunião correspondente. Ler os relatórios existentes como estão e tratar campos novos como opcionais.
- Em **Reuniões e Planos**, o operador pode iniciar uma reunião por pauta, acompanhar intervenções, consultar o plano registrado pelo gestor e exportar em Markdown. Salvar incrementalmente os registros vinculados ao atendimento e, quando houver, ao projeto. Para reuniões anteriores, mostrar os dados efetivamente disponíveis.
- **Zelda Manager Group** incorpora o painel existente à janela, adapta desenho e áreas clicáveis ao tamanho disponível e oferece acesso por teclado aos modelos, salas, consumo e ações.

## Ordem de entrega e critérios de aceite

1. **Janela e chat.** Validar bandeja, atalhos, voz → texto → voz no mesmo contexto, gravação cancelada e enviada, chat silencioso, reabertura durante tarefa e navegação a 100%, 150% e 200% de escala.
2. **WhatsApp integrado.** Verificar um perfil já conectado. Com um perfil isolado e desconectado, testar QR dentro da seção, renovação, cancelamento, expiração e leitura real pelo celular. Concluir com uma consulta pelo mesmo perfil após reiniciar a Frida. Verificar que o QR não foi persistido.
3. **Projeto local de ponta a ponta.** Criar um projeto numa pasta temporária, pedir um script pelo chat, abri-lo em Código e executá-lo. A primeira linha deve aparecer no Terminal enquanto o processo ainda está em execução. Conferir arquivo em disco, saída de erro, cancelamento, conflito de edição e vínculo no Histórico. Repetir o disparo por voz e pela equipe.
4. **Projeto em VPS.** Em um destino SSH de teste, abrir uma raiz remota, salvar por SFTP, executar e verificar a saída no host indicado. Trocar o projeto visível durante a execução, simular queda de conexão e conferir recuperação sem repetição automática. Homologar SSH real separadamente dos testes simulados.
5. **Registros e instalação.** Validar reuniões, planos e gerenciamento de arquivos após reinício, além dos relatórios antigos. Compilar uma candidata com a suíte C# e Python atual, testar o conjunto Windows e worker Python da mesma revisão, instalar com backup e verificar o heartbeat. Preservar perfil do WhatsApp, cache, configurações e dados de voz na atualização e na restauração.
