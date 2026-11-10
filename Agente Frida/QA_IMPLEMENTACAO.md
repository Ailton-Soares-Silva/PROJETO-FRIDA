# Execução do QA da IDE Frida

Objetivo: executar integralmente o plano aprovado a partir de `QA FRIDA 01.pdf` (duas páginas). Este arquivo acompanha trabalho em andamento; não é declaração de conclusão.

## Decisões confirmadas

- Chat e Código com conversas, projetos e reuniões; WhatsApp e Zelda permanecem na navegação principal.
- Mudança de assunto entre Chat/Código cria conversa vinculada, preservando a origem.
- Reuniões contínuas, mensagens identificadas de cada modelo e artefatos MD no painel direito.
- Lixeira restaurável; excluir projeto preserva a pasta e seus arquivos.
- Terminais Motor/Execução contextuais à direita; editor existente integrado a Código.
- Zelda preenche a área disponível, sem barras externas redundantes nem formulário inferior de reunião.

## Estado em 08/11/2026

Implementado nesta etapa, ainda sem ativação na interface:

- `FridaConversationStore`: catálogo versionado, conversas/projetos identificados, vínculo de origem, rascunhos/anexos, mensagens paginadas, atualização idempotente de mensagens, exclusão/restauração em cascata e bloqueio de exclusão durante execução.
- Campos opcionais de identidade no contexto e nos relatórios existentes; compatibilidade com relatórios sem esses campos.
- Seis testes de persistência, paginação além de 80 mensagens, Lixeira, arquivos físicos, concorrência, catálogo inválido e ocultação de credenciais.
- Backup dos fontes anteriores em `scratch/qa-workspace-baseline-20261108-201245`.

Decisão técnica ajustada: mensagens em arquivos JSON individuais, com gravação atômica e nomes ordenáveis por horário, em vez de um JSONL mutável. A paginação lê somente o conteúdo da página solicitada; uma gravação interrompida não trunca mensagens anteriores. O catálogo inválido falha explicitamente e não é substituído silenciosamente.

## Trabalho obrigatório restante

1. Migrar relatórios/projetos/reuniões de forma idempotente, preservando os originais e vinculando por request_id. Testar recuperação de migração interrompida.
2. Integrar armazenamento à entrada de texto/voz, separar memória e pendências do cérebro por conversa, fixar contexto de execução e encaminhar eventos/respostas ao destino original.
3. Implementar navegação compartilhada, criação/retomada/pesquisa/renomeação/Lixeira, rascunhos, arquivos, editor e correção manual de seção; atualizar atalhos e entradas antigas.
4. Implementar roteamento Chat/Código e conversa vinculada com contexto inicial; testar tarefas cotidianas que usam ferramentas sem serem desenvolvimento.
5. Adaptar reuniões a rodadas contínuas e eventos individuais; retirar limite permanente de 80 mensagens; versionar planos/fluxos e vincular skills reais com estado salvo/verificado.
6. Implementar terminais Motor/Execução filtrados por conversa, streaming, interrupção correta, persistência de layout e proteção de credenciais.
7. Recalcular geometria de ambas as partes do Zelda, desenho e áreas clicáveis; manter ações internas e teclado.
8. Homologar fluxos completos após reinício e em DPI 100/150/200; testar regressões de voz, WhatsApp e SSH; empacotar candidata, backup/migração/rollback e atualização somente após validação.

## Segunda etapa: migração e memória do cérebro

- Adicionado `FridaConversationMigration.cs`: importação repetível, identidade determinística por atendimento, junção de reuniões por request_id, mensagens com IDs estáveis e retomada após gravação parcial. Os originais são somente lidos; registros inválidos produzem avisos. Itens na Lixeira não são reativados.
- Adicionado `FridaConversationBrain.cs`: memória, últimas respostas e pendências nativas/WhatsApp separadas por ConversationId e persistidas em brain.json. Troca de contexto ocorre sob o gate de atendimento. Retomada de histórico importado utiliza somente mensagens disponíveis.
- O cérebro já chama ativação/persistência, porém a interface ainda não fornece ConversationId: integrar a criação/seleção/envio é a próxima etapa necessária para ativar o comportamento de ponta a ponta.
- Armazenamento padrão novo: `IdeData/Workspace`. Dados reais ainda não migrados. Migração deve ser chamada pela inicialização do novo workspace com ConversationReports, IdeData/projects.json e IdeData/Meetings; exibir avisos retornados.
- Verificação direcionada disponível: `tests/run_tests.ps1 -WorkspaceOnly`, compilando todos os fontes e executando somente os testes dessa etapa. Dez testes aprovados na segunda etapa. A suíte completa e suas seis pendências anteriores continuam obrigatórias antes da entrega.

## Terceira etapa: integração inicial da interface

- Adicionado `FridaWorkspaceForm.cs`: navegação principal reduzida a quatro seções, árvore com conversas/projetos/reuniões, busca por título, criação/seleção/renomeação, mudança manual de seção, Lixeira e restauração por menu de contexto. Histórico, arquivos e terminal deixam de ocupar botões principais.
- Chat e Código compartilham o workspace; Código tem aba Editor com o editor existente. Painel direito recolhível contém Arquivos e Execução existentes. A aba Artefatos ainda é um placeholder e precisa ser implementada; o terminal Motor também falta.
- Inicialização normal importa registros para IdeData/Workspace. Construtor aceita armazenamento isolado para testes sem importar dados reais. Ainda não iniciada uma candidata normal nem instalada atualização.
- Envios recebem ConversationId/ProjectId/contexto por AsyncLocal, preservados pelo tray até o cérebro. Audit.Save persiste mensagens vinculadas; pedidos são registrados antes de iniciar trabalho. O envio protege a conversa contra exclusão e atualiza somente a conversa de origem.
- Rascunhos/anexos persistem ao navegar; áudio pendente é preservado em memória por conversa. Troca durante captura/transcrição é impedida. Reunião selecionada usa a rota de equipe; ainda falta continuidade real das rodadas e eventos individuais.
- Teste novo abre a Form isolada, inicia resposta pendente em A, troca para B, conclui A e confere mensagem somente em A e rascunho B intacto. Onze testes direcionados aprovados.
- Próximas prioridades: integrar voz ao ConversationId, roteamento automático com vínculos, reuniões contínuas/artefatos, terminais estruturados, menus de projeto incluindo VPS/criação, busca no conteúdo, estado de layout e atalhos para registros exatos. Inspeção visual e acessibilidade ainda pendentes. Revisar atualização de rascunho da conversa de origem se houver edição durante envio.

## Quarta etapa: roteamento, voz e reuniões

- Adicionado `FridaWorkspaceRouting.cs`: classificação de seção separada do uso de ferramentas, mudança de seção por conversa vinculada, projeto preservado, contexto copiado sem pendências operacionais e respeito à correção manual. Continuação ambígua mantém a conversa. Voz passa a fixar ConversationId/projeto e proteger a conversa durante o atendimento; UI acompanha a conversa de voz quando ela permanece selecionada.
- FridaTeamState publica cada contribuição identificada imediatamente no armazenamento da conversa. Rodadas têm RequestIds próprios e compartilham ConversationId; retirado descarte após 80 mensagens. Modelos indisponíveis aparecem com estado correspondente. Memória anterior/última síntese continua sendo retomada pelo cérebro.
- Adicionado `FridaMeetingArtifacts.cs`: versões imutáveis de planos, fluxos e conclusões; planos/fluxos vêm explicitamente do coordenador. Skills são vinculadas após registro, com estado salva/verificada e conteúdo real de SKILL.md.
- Aba Artefatos agora lista versões e apresenta Markdown com exportação. Eventos de reunião atualizam a conversa visível. Chamadas de reunião pela navegação Zelda encaminham ao novo chat; relatório de execução não substitui contribuições individuais.
- Treze testes direcionados aprovados, incluindo encaminhamento Chat→Código com projeto/contexto e sem comandos pendentes; 83 contribuições em duas rodadas, disponibilidade dos participantes, versões anteriores de plano/fluxo e reabertura do armazenamento.
- Ainda obrigatórios: terminais estruturados e filtrados, ajuste/layout do Zelda, manutenção dos recursos de projeto local/VPS, pesquisa de conteúdo e atalhos precisos, rascunhos concorrentes, testes abrangentes (incluindo seis falhas anteriores), inspeção visual/DPI, candidata e instalação. Fluxo físico de voz ainda não homologado. Conferir preservação de contexto de reuniões extensas no orçamento enviado aos modelos e evitar duplicação ao reimportar arquivos legados de reuniões.

## Quinta etapa: terminais e Zelda

- `FridaDiagnostics.cs`: eventos com conversa/atendimento/projeto/destino/diretório, operação quando disponível, UTC, tipo motor/execução, texto protegido e persistência JSONL limitada/rotacionada. Leitura tolera última linha truncada. Integrado a logs do motor, início/fim de operações, streaming de processos e recibos com duração.
- Painel Terminais agora tem duas áreas Motor e Execução. Seleção de conversa carrega apenas seus registros; eventos tardios de outra conversa são ignorados pela tela atual. Arquivos do terminal filtrados por conversa. Interrupção no compositor/terminal verifica a conversa ativa antes de cancelar. Execução do editor recebe ConversationId e lease.
- Zelda sem barras externas, status duplicado e formulário inferior. Geometria lógica calculada do espaço real, com salas/HUD/lateral recalculados e mesma transformação para cliques/roda. `FridaZeldaAccessibility.cs` mantém ações pelo menu nativo e seleção de salas com Alt+1…7.
- Quinze testes direcionados passaram: inclui saída durante processo, segregação de diagnósticos, credenciais, linha truncada, preenchimento de área e clique nas abas em 860×600, 1380×800 e 1720×1200. Essas dimensões não substituem ainda homologação real de DPI/tecnologia assistiva.
- Imagens em `scratch/qa-renders`: Zelda revisado visualmente em 860×600 e 1380×800. Teste renderiza janela de workspace fora da tela; detectado e corrigido painel de chat oculto após reparenting. Workspace com painel de terminais foi conferido visualmente após correção.
- Restante antes de concluir: persistir layout, repor criação/local/VPS/gestão de projetos em menus contextuais, pesquisa de conteúdo, atalhos para item correto, fixar contexto em salvamento do editor e operações de arquivos, corrigir rascunho editado durante envio, revisar reimportação/reuniões, completar acessibilidade/DPI e regressões (incluindo seis falhas antigas), empacotar/homologar candidata e instalar com backup e preservação dos dados.

## Sexta etapa: finalização e auditoria (08/11/2026, após a interrupção do Codex por limite de uso)

O Codex parou às 20:47 por `usage_limit_exceeded`, depois de concluir a quinta etapa com 15 testes direcionados aprovados. As pendências foram concluídas e a implementação foi auditada.

### Pendências concluídas

- **Projetos local/VPS**: botão "Projeto…" (abrir ou criar no computador ou na VPS, conferir identidade SSH). Menu do projeto na árvore: nova conversa ou reunião, renomear, testar conexão SSH, excluir e restaurar. O catálogo do workspace passou a ser o cadastro único e o `projects.json` legado é apenas importado.
- **Busca no conteúdo**: a partir de 3 letras, a busca também procura no texto das mensagens, sem diferenciar maiúsculas nem acentos. A digitação espera uma pausa antes de pesquisar e os itens da Lixeira ficam fora da busca normal.
- **Layout persistente**: o painel direito, a proporção das colunas, a divisão Motor/Execução e a aba ativa ficam em `layout.json`. Só arrastos do usuário alteram as proporções.
- **Atalhos precisos**: a tecla de atalho "última conversa" abre a conversa exata do último atendimento. A sala de reunião do Zelda abre a última reunião.
- **Contexto do editor e das operações de arquivo**: a gravação do editor vai para a conversa que abriu o arquivo. Criar, mover, renomear e enviar à lixeira aparecem no terminal Execução da conversa selecionada.
- **Rascunho no envio**: só é apagado o que foi enviado. Na mudança de assunto, o texto vai para a conversa vinculada e não fica como rascunho na origem.
- **Vínculos nos dois sentidos**: o botão "Vínculos" e um aviso no histórico das duas conversas ligam origem e continuação. Esses avisos não entram no contexto do modelo.
- **Rodada interrompida identificada**: um pedido sem resposta registrada recebe a marca "Rodada interrompida…" e nunca é reexecutado automaticamente.
- **Skills**: os estados **proposta**, **salva** e **verificada** são exibidos separados. Uma proposta do Gestor (`proposed_skills`) nunca substitui uma versão salva ou verificada.

### Defeitos encontrados na auditoria e corrigidos

1. **Reimportação a cada abertura da IDE**: todos os relatórios eram reprocessados e o catálogo era regravado milhares de vezes; com 2.237 relatórios de teste, a abertura passava de minutos. Agora a importação roda em lote na memória, com gravação periódica, e o que já foi importado é pulado sem tocar no catálogo. Com uma cópia dos dados reais (292 relatórios e 3 reuniões): primeira migração em 3,9 s, reabertura em 1 s sem regravar o catálogo, originais intactos.
2. **Limite de caminho (MAX_PATH)**: o temporário de mensagem chegava a ~247 caracteres na instalação real, a 13 do limite, e falhava em pastas mais longas. O sufixo temporário foi encurtado, há aviso quando a raiz passa de 125 caracteres e um teste no limite.
3. **Reunião dividida**: quando o assunto mudava, a reunião se dividia em outra seção. Agora ela permanece um único registro contínuo.
4. **Conversa da Lixeira recebendo voz**: uma seleção antiga da voz falhava ou reaproveitava uma conversa excluída. Agora o pedido cria uma conversa nova.
5. **Catálogo corrompido**: a IDE não abria. Agora há uma cópia `workspace.previous.json` e a restauração é oferecida ao abrir, preservando o arquivo inválido.
6. **Envio em conversa vinculada**: o texto enviado voltava como rascunho da origem.
7. **Projeto ativo do envio**: passou a sair da conversa, e não da seleção visual.
8. **Testes da IDE no armazenamento compartilhado**: com rascunhos persistentes, o rascunho de um teste vazava para o seguinte. Cada janela de teste agora usa um armazenamento isolado.

### Verificação

- Workspace: 25 testes direcionados aprovados (`tests/run_tests.ps1 -WorkspaceOnly`), sendo 10 novos.
- Suíte C#: **224 aprovados e 0 falhas** depois da restauração da voz 1.3.0.11 (antes eram 219 aprovados e 5 falhas de voz). A bateria de código ficou em 30/30, sem conclusões falsas.
- Candidata final: executável com SHA-256 `7D089FB8E14DD07F35DF87CD78C6E4C2CB522EFC837E4B6773FC4ADC304F91A6`. Os quatro fontes de voz no pacote são idênticos aos da 1.3.0.11.
- Python: 79 testes OK (5 pulados por dependências opcionais de avaliação de voz).
- Orçamento de tokens: o teste foi alinhado à política vigente de `FridaTaskRouting` (8192/16384/32768), alterada em 08/11 às 19:42.
- Renderização: workspace e terminais conferidos; Zelda em 860×600, 1380×800 e 1720×1200.
- Candidata `scratch/release-ide-1.3.0.12` com `package.json` (hashes do executável e de 50 fontes). O teste do instalador aprovou instalação, rollback, preservação do `config.json`, rejeição de pacote adulterado e reversão de falha parcial.

### Cinco falhas de voz pendentes de decisão

Na versão 1.3.0.11 (14:04) todos os testes passavam. Depois disso, `FridaSessionControl.cs` (14:45), `SpeakerVoiceProfile.cs` (14:53) e `LocalWakeDetector.cs` (19:42, no mesmo segundo do `config.json`) foram alterados fora desta sessão:

- "para" deixou de ser comando de parada;
- a parada passou a exigir "Frida";
- o SNR de comando deixou de ser verificado;
- trechos curtos de outro locutor e embeddings inválidos passaram a ser liberados.

Os testes de 14:03 rejeitam esse afrouxamento.

**Decisão do operador (08/11/2026): restaurar a voz da 1.3.0.11.** `FridaSessionControl.cs`, `LocalWakeDetector.cs`, `SpeakerVoiceProfile.cs` e `AudioVoiceEngine.cs` voltaram a ser idênticos aos fontes de `scratch/release-voice-1.3.0.11/sources` (hashes conferidos). As versões afrouxadas ficaram em `scratch/voice-relaxed-before-restore-20261108`.

Junto com elas foram revertidos:
- a exigência de dizer "Frida" para parar;
- a parada sem o bloqueio de locutor durante a fala da Frida;
- a omissão do SNR em comandos;
- a liberação de trechos curtos de outro locutor e de embeddings inválidos;
- a regra que ignorava controles durante o ditado ativo.

Efeito conhecido: "para" volta a ser comando de parada. O bloqueio de locutor continua sendo a proteção contra o eco da própria Frida.

### Instalação e restauração

**Instalada em 08/11/2026 às 21:34** (PID 17864, heartbeat confirmado). Na pasta real, a migração criou 292 conversas (3 reuniões) e 614 mensagens, sem avisos e sem temporários órfãos. Backup em `scratch/release-ide-1.3.0.12/backup-20261109-003440-7432fba9`.

1. Fechar a Frida pelo menu Sair.
2. `powershell -ExecutionPolicy Bypass -File "scratch\release-ide-1.3.0.12\install_release.ps1" -Release "scratch\release-ide-1.3.0.12\FridaAgent.exe" -PackageManifest "scratch\release-ide-1.3.0.12\package.json" -TargetDirectory "<pasta Agente Frida>"`. Informe sempre `-TargetDirectory`: rodando a cópia que fica dentro do pacote sem esse parâmetro, o destino seria a pasta `scratch`.
3. O instalador guarda o executável anterior e uma cópia de `IdeData` e `ConversationReports` em `backup-*/data`, registrada em `rollback.json`.
4. Para reverter o executável, use `install_release.ps1 -RollbackManifest <backup>\rollback.json -TargetDirectory "<pasta Agente Frida>"`. Para reverter os dados, com a Frida fechada, copie `backup-*\data\*` de volta para a pasta da Frida. A migração só cria `IdeData\Workspace`; os originais nunca são alterados.

Pendentes de homologação física: voz real, DPI 150/200% no monitor, leitor de tela e SSH ao vivo.

## Sétima etapa: redesenho do layout da IDE (08/11/2026)

Plano aprovado: lateral com filtro segmentado, densidade equilibrada e escopo somente na IDE.

**Sistema visual** (`FridaUiKit.cs`):
- tokens de cor;
- escala única por DPI, com `ScaleOverride` para testes;
- fontes em pixels, que acompanham a escala;
- `PillButton` com variantes primária, secundária, fantasma, perigo e ícone, em 36 px (32 px na versão compacta);
- `ActionBar` em linha única: o que não cabe vai para o menu "…", sem quebrar nem sobrepor;
- `SegmentedTabs` e `HeadlessTabControl`;
- `InputFrame` com lupa, botão limpar e texto de dica;
- menus escuros arredondados;
- `FridaDialog` no lugar do `Prompt` e das `MessageBox` da IDE;
- `FridaConversationTree`, a árvore de conversas com desenho próprio;
- prévia de Markdown dos artefatos.

**Estrutura:**
- trilho só de ícones (Ctrl+1–4);
- lateral com:
  - "Nova conversa" e menu para reunião e projetos local/VPS;
  - busca (Ctrl+K);
  - filtro Conversas | Projetos | Reuniões, com agrupamento por data e projetos indentados;
  - Lixeira no rodapé.
- cabeçalho da conversa com título editável, chips de seção, projeto/destino, vínculos e execução, além de Conversa | Editor no modo Código;
- painel direito Artefatos | Arquivos | Terminais (Ctrl+J), com cabeçalhos próprios;
- editor sem árvore duplicada, com abas próprias, estado vazio e rodapé de linha/coluna;
- WhatsApp em cartão de status;
- reuniões em estilo chat em grupo, com avatar, modelo e horário.

O `AutoScaleMode` foi desligado para não aplicar a escala duas vezes. As páginas antigas (Histórico, Projetos, Reuniões, Planos) foram removidas.

**Verificação:**
- novo teste de integridade em 84 combinações (escalas 100/150/200% × 4 tamanhos × 7 telas): nenhuma sobreposição; nenhum controle cortado por qualquer ancestral; alvos de clique ≥ 32 px escalados; nenhum fundo claro;
- os testes redimensionam a janela por `SetWindowPos`, porque o `Form` do WinForms limita o tamanho à tela;
- suíte C# com 225/225 aprovados e Python com 79 OK;
- candidata `scratch/release-ide-1.3.0.13` com o teste do instalador aprovado.

**Instalada em 08/11/2026 às 22:25** (PID 20008, heartbeat confirmado, versão 1.3.0.13). Backup em `scratch/release-ide-1.3.0.13/backup-20261109-012447-36c2746c`.

A primeira tentativa de instalação parou antes de trocar qualquer arquivo. A cópia simples de `IdeData`, aninhada dentro da pasta de backup, passava de 260 caracteres nos arquivos de mensagem. Agora o instalador guarda `data/data-IdeData.zip` e `data/data-ConversationReports.zip`, confere a quantidade de arquivos de cada zip (807 e 293) e foi testado com caminhos profundos. Para restaurar os dados, com a Frida fechada, extraia cada zip na pasta de mesmo nome da instalação.

**1.3.0.14 — laterais redimensionáveis e sem rolagem horizontal:**
- **Lista lateral:** a divisória arrastável fica destacada ao passar o mouse. A largura vai de 200 a 560 px (na escala 100%), é salva em `layout.json` e o duplo clique restaura 280 px.
- **Painel direito:** o limite fixo de 640 px saiu. Agora só valem os mínimos: 380 px para a conversa e 260 px para o painel. O duplo clique na divisória restaura a proporção padrão, e a divisória Motor/Execução também tem destaque e duplo clique.
- **Rolagem horizontal eliminada:**
  - na lista (estilo `TVS_NOHSCROLL`, já que os títulos são cortados com reticências);
  - na conversa (a largura agora sempre reserva a barra vertical, que antes provocava 1 px de excesso).
- **Editor:** botão "Quebra de linha" (Alt+Z), lembrado entre sessões. A numeração e o "Ln, Col" seguem as linhas reais do arquivo.
- **Verificação:** suíte C# com 226/226 aprovados (com teste novo de arraste, persistência, limites e ausência de rolagem horizontal) e Python com 79 OK. Candidata `scratch/release-ide-1.3.0.14` com o teste do instalador aprovado.
- **Instalada em 08/11/2026 às 22:51** (PID 4128, heartbeat confirmado, sem erros no log). Backup do executável e dos dados (807 + 293 arquivos em zip) em `scratch/release-ide-1.3.0.14/backup-20261109-015106-aae0ffd5`.

**Diferença em relação ao plano:** as ações das mensagens (ouvir, copiar) ficam sempre visíveis, mas pequenas e discretas, em vez de aparecerem só ao passar o mouse, para manter o acesso por teclado e leitor de tela.

## Oitava etapa: QA FRIDA 02 e varredura UI/UX (09/11/2026)

Origem: PDF "QA FRIDA 02" (4 itens) e varredura com as instruções do agente ui-ux-designer (17 achados). Escopo decidido pelo operador: os 4 itens mais os achados de severidade Alta e Média. Relatório final como card no chat mais relatório completo salvo.

**Chat (QA 1):**
- Balões: "Você" à direita; Frida e membros do time à esquerda, com avatar (membros do time com barra na cor do papel); avisos como pílula centralizada.
- Mensagens seguidas do mesmo autor, em até 5 minutos, ficam agrupadas.
- Markdown renderizado por `FridaMarkdown.ToRtf`: títulos, negrito/itálico, código, listas aninhadas, citações, links e tabelas (em colunas ou como "coluna: valor"). Sublinhado dentro de palavra continua texto (`auth_required`).
- Respostas do time em JSON (plano, decisão) viram itens com rótulos.
- Copiar põe RTF e texto limpo na área de transferência; o menu de contexto tem "Copiar como Markdown". Ouvir lê sem os símbolos.
- Conversa só acrescenta as mensagens novas; quando precisa reconstruir, o desenho fica suspenso.
- Corpo das mensagens fora da ordem de Tab e sem pulo de rolagem ao clicar.
- Indicador "Frida está trabalhando…" e linha de aviso com posição fixa.

**Caixa de digitação (QA 2):**
- Anel de foco de 2 px com halo; bordas com contraste de 3:1 (token `EdgeStrong`).
- Placeholder desenhado pela própria caixa, sem cobrir o cursor.
- Cresce de 1 a 10 linhas; a barra de rolagem só aparece e some (via `ShowScrollBar`, sem recriar o controle).
- Colagem com mais de 2.000 caracteres ou 40 linhas vira chip de anexo (`Conversations\<id>\Anexos\colado-*.txt`) e vai junto na mensagem; o balão mostra só uma linha. Ctrl+Z desfaz. Arquivos copiados no Explorer viram referências.
- Rascunho gravado após 500 ms de pausa, não a cada tecla.

**Lista lateral (QA 3):**
- "⋯", botão direito e Shift+F10 abrem o menu sem selecionar nem abrir a conversa.
- Renomear no lugar (Enter salva, Esc cancela); Excluir com "Desfazer" no rodapé.
- A lista atualiza no lugar quando as linhas não mudaram e preserva a rolagem quando mudam; nada é reconstruído com o menu aberto.
- `ShowSection` não esconde e mostra a página atual (a janela piscava); a árvore é reconstruída uma vez por seleção, não duas.
- A dica das abas do editor não pisca mais.

**Decisão final da secretária (QA 4):**
- O Gestor decide em JSON (veredito, feito, pendente, não feito, justificativa, próximos passos, atualização de skill). O veredito nunca afirma mais do que a revisão e os recibos sustentam.
- Prints de evidência em `Conversations\<id>\Evidence\ev-NNN.jpg`, com SHA-256: a tela analisada pelo `inspect_screen` e a tela ao final, quando houve ferramenta de tela, navegador ou janela.
- Lições aprendidas acrescentadas à skill citada, com versão e nova verificação, somente com veredito "Aprovado".
- Artefato versionado "Decisão final" (Markdown e JSON) e card no chat com veredito, contagens, até 3 prints e "Ver relatório completo". A janela do relatório exporta Markdown (com os prints) e Word.
- O relatório da secretária não é mais sobrescrito pela resposta final.

**Outras correções:**
- Vazamento de `Font` a cada quadro no Zelda.
- Sala de reunião do Zelda só remonta a transcrição quando ela muda, e sem símbolos.
- Popup de resposta em texto: renderizado, com rolagem, espera o mouse sair, fecha com × ou Esc.
- Voz Agy sem vazamento de fonte e sem símbolos.
- Tela inicial descartava as fontes compartilhadas (X vermelhos nos controles); o Antigravity corrigiu o mesmo ponto em paralelo.

**Mescla com o trabalho simultâneo do Antigravity:** feita em cópia isolada (`scratch/qa02-work`), com mescla de 3 vias. Backups da pasta real: `scratch/qa02-live-before-merge-20261109-103755` (primeira tentativa, revertida) e `scratch/qa02-live-before-merge-20261109-115853`.

**Correções nas mudanças do Antigravity (pedido do operador: não deixar falhas):**
- `RunPowerShell` voltou a devolver `ProcessCompleted` para código 0. Marcar como `Verified` derrubava a aprovação de compilação e testes: bateria de código em 0/30. A verificação de janela ao abrir programas foi mantida.
- `SpeakerVoiceProfile.cs` voltou a ser idêntico à 1.3.0.11 (o SNR de comando tinha sido baixado para 1 dB).
- Quebras de linha CR CR LF normalizadas para CRLF.
- `tests/run_tests.ps1` recebeu as referências WindowsBase/UIAutomation exigidas pelo `FridaWindowVerification.cs`.

**Verificação:**
- Suíte C# com 232/232 aprovados (6 testes novos de workspace); bateria de código 30/30 sem conclusões falsas; Python com 79 OK (5 pulados por dependências opcionais).
- Testes de workspace com `FRIDA_QA_RENDER_DIR`: 33/33, 47 imagens. O travamento antigo nesse modo vinha das fontes descartadas pela tela inicial.
- Candidata `scratch/release-ide-1.3.1.0` (versão 1.3.1.0) com o teste do instalador aprovado: rollback, `config.json` preservado, pacote adulterado rejeitado e falha parcial revertida.
- **Instalada em 09/11/2026 às 12:13**, com a confirmação do operador. A Frida já estava fechada (menu Sair às 11:50). PID 27572, heartbeat confirmado na versão 1.3.1.0, SHA-256 `A844BD5832CF4F79E3EF2BC33BFF188D5D499D43416E3A5951D269519186ECBC`, inicialização sem erros no log.
- Backup do executável e dos dados (zips de `IdeData` e `ConversationReports`) em `scratch/release-ide-1.3.1.0/backup-20261109-151304-b9fcf415`. Para reverter: `install_release.ps1 -RollbackManifest <backup>\rollback.json -TargetDirectory "<pasta Agente Frida>"`.

### 1.3.1.1 — tela inicial (09/11/2026)

Pedido do operador: logo dinâmico grande e destacado, sem frases cortadas.
- O logo é desenhado com uma transformação de escala sobre o desenho original (raio de 84 px), mantendo as proporções. Cresce até 190 px de raio, cerca de 2,3 vezes maior, limitado ao espaço deixado pelo texto e a 44% da largura.
- O título (24 pt) e o subtítulo são medidos com a fonte real e quebram linha em janela estreita. Antes, uma caixa fixa de 32 px cortava os descendentes do título e o encostava no subtítulo.
- O anel do clique continua em coordenadas de tela.
- Teste novo em 100/150/200% e três tamanhos: logo grande, nenhum texto cortado, fora da tela ou sobreposto ao logo ou aos atalhos.
- Suíte C# 233/233; bateria de código 30/30; Python 79 OK.
- Candidata `scratch/release-ide-1.3.1.1` com o teste do instalador aprovado.
- **Instalada em 09/11/2026 às 14:59**, com a confirmação do operador (Frida fechada pelo menu Sair às 14:58). PID 164, heartbeat na versão 1.3.1.1, SHA-256 `2FB69F82F82C56250D7FC03B4E30D8C9BAB2E934F571A1C2E6CD934A47873040`, inicialização sem erros.
- Backup em `scratch/release-ide-1.3.1.1/backup-20261109-175904-8eb98896`; para voltar à 1.3.1.0, use o `rollback.json` desse backup.

### 1.3.2.0 — caixa, detalhes técnicos e projetos (09/11/2026)

Pedido do operador: limpar a caixa ao enviar, tirar da conversa o código que polui as respostas e permitir mover conversas existentes para projetos, com o projeto ganhando o contexto delas.
- **Caixa de digitação:** esvazia no momento do envio (antes só esvaziava quando a resposta inteira chegava). Se o envio falhar, texto, anexos e áudio voltam para a caixa, ou ficam como rascunho daquela conversa.
- **Respostas:** `FridaReplyParts` separa a resposta da evidência técnica (código de saída, stderr, listagens, leitura de arquivo, janelas confirmadas, recibos JSON). O balão mostra a resposta. Uma mensagem que é só um recibo vira uma linha ("Recibo da ferramenta …"). Blocos de código com mais de 12 linhas mostram as 6 primeiras. Tudo fica em "Detalhes técnicos · N linhas", que abre e fecha dentro do balão. "Copiar" leva tudo; "Ouvir" lê só a resposta. A evidência continua gravada sem alteração.
- **Projetos:** "Mover para projeto" no menu da lista, com os projetos existentes e "Remover do projeto" (`FridaConversationStore.AssignProject`; recusado com a conversa em execução). A conversa aberta acompanha o projeto.
- **Memória do projeto:** `ProjectMemory` reúne título e última troca das outras conversas do projeto, sem saída técnica, até 4.000 caracteres. Ela entra no prompt de todo pedido feito no projeto, por `FridaIdeRuntime.ProjectInstructions`.
- **Verificação:** C# 236/236 (3 testes novos), bateria de código 30/30, Python 79 OK. Candidata `scratch/release-ide-1.3.2.0` com o teste do instalador aprovado.
- **Instalada em 09/11/2026 às 15:37**, com a confirmação do operador. PID 5012, heartbeat na versão 1.3.2.0, SHA-256 `17B46C6F8A2D42B01F75C146458F4AFCA362ECDDC9C7551F7B069E951ACAB3F8`, inicialização sem erros. Backup em `scratch/release-ide-1.3.2.0/backup-20261109-183702-de3cd3e4`.

## Verificação da primeira etapa

Comando: `powershell -NoProfile -ExecutionPolicy Bypass -File tests/run_tests.ps1`.

Log: `scratch/qa-workspace-tests.log`.

Execução concluída: **199 testes C# aprovados, seis falhas**. Os seis testes novos de Workspace passaram. O script não executou Python porque interrompe após falha C#. Além do orçamento abaixo, falharam cinco cenários de voz: `para` em duas rotas, qualidade de cadastro com ruído, trecho curto de outro locutor e comando curto sob reprodução. Esses componentes não foram alterados nesta etapa, mas as falhas devem ser investigadas e verificadas antes da homologação final. A compilação do conjunto passou. Nenhuma migração de dados reais nem instalação foi executada.

A suíte encontrou uma divergência anterior na expectativa de orçamento de tokens de CodingCopilotTests (8192/16384) versus FridaTaskRouting (16384/32768). O SHA-256 de FridaTaskRouting é igual ao backup inicial: `2E127A09EDEE4356AFD59B788C69439AE35076236FDA2C45D19C1C4E042F8E86`. Investigar e alinhar o teste à política vigente antes de considerar a suíte aprovada. Não alterar o orçamento de modelos incidentalmente durante a reorganização da IDE.
