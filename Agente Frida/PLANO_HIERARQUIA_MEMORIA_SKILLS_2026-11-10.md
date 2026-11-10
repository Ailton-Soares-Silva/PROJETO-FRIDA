# Frida — auditoria de hierarquia, memória e skills + plano de melhoria

## Contexto

Objetivo do operador: a Frida deixar de ser um assistente que reage a palavras-chave e passar a agir como ChatGPT / Claude / Antigravity — entender o pedido, pesquisar na web, montar o próprio método e resolver — dentro de uma interface multi-IA em que cada modelo trabalha no que faz de melhor.

A auditoria (leitura direta do código em `Agente Frida/`) mostra que a base de **verificação** é forte (recibos, ledger, ciclos de correção, relatório de decisão), mas o **entendimento, a delegação, a memória e as skills** ainda são dirigidos por regex e por um roteiro fixo. O plano abaixo troca isso por planejamento feito pelo modelo, mantendo os recibos como trava final.

---

## Auditoria — o que existe e o que falta

### 1. Sistema hierárquico

| Achado | Onde | Efeito |
|---|---|---|
| Roteamento é regex de verbos/substantivos; a triagem por modelo só classifica em 9 tipos fixos e é sobrescrita em vários ramos | `FridaTaskRouting.Decide` (FridaTaskRouting.cs:121), `FridaRequestIntent.Classify` (FridaOperational.cs:56), `FridaEconomyRouting.Apply` (FridaEconomyRouting.cs:120) | Pedido sem a palavra certa cai em "Conversa" ou no tipo errado |
| A equipe é um roteiro linear fixo: Gestor → pesquisa → Estagiário → execução → Sênior → skill → Analista → correção → decisão → Secretária | `FridaTeamCoordinator.RunAsync` (FridaTeam.cs:547) | O Gestor não decide o fluxo; todo pedido "de equipe" paga as mesmas ~8 chamadas |
| O plano JSON do Gestor (`delegated_roles`, `workflow`, `missing_requirements`) só tem `research_queries` lido; o resto vira texto colado no prompt do executor | FridaTeam.cs:578, FridaTaskRouting.cs:214 | Delegação é decorativa |
| Gestor, Analista, Estagiário e Sênior são chamadas de texto únicas, sem ferramentas, sem histórico, sem memória, sem catálogo de skills | `FridaTeamClient.QueryCoreAsync` (FridaTeam.cs:319), `Ask` (FridaTeam.cs:522) | Planejam e auditam "às cegas", só sobre texto truncado em 24 000 caracteres |
| Quem executa ferramentas não é o papel mostrado no painel: o loop usa as rotas ordenadas por preferência/cota (Groq, Cerebras, DeepSeek, Kimi) e depois a cascata Gemini | `BuildActiveOpenAiRoutesAsync` (AgentBrainAndTray.cs:3151), `ModelQuotaGuard.OrderByPreference` (LlmUsageLedger.cs:819) | "Engenheiro / Kimi K3" aparece trabalhando mesmo quando outro modelo fez o trabalho; não há "cada IA no que faz de melhor" |
| Forças dos modelos estão em strings e fallbacks duplicados por papel | FridaTeam.cs:326-341 e 418-436 | Trocar/adicionar modelo exige editar código em vários pontos |
| Decisões internas da equipe também por regex (`needsResearch`, `needsSeniorResearch`, `multiAgentOrCross`) | FridaTeam.cs:582, 612; FridaTaskRouting.cs:198 | "planilha", "placa", "gestor" forçam equipe/sênior mesmo sem necessidade |
| Uma reunião por vez, tudo sequencial | `_gate.WaitAsync(0)` (FridaTeam.cs:550) | Sem subagentes paralelos |
| Pesquisa web é uma resposta única do Gemini com `google_search` (cortada em 3 500 caracteres) ou resumos RSS; não há ferramenta para abrir e ler uma página | `PerformGoogleSearchResearchAsync` (AgentBrainAndTray.cs:6493), `PerformZeroQuotaWebRssSearchAsync` (:6789) | Não consegue ler documentação/tutorial para "construir sua forma de fazer" |
| Todas as travas de ferramenta retornam `true`; scripts escritos por modelo rodam com acesso total sem confirmação | `OffersTool/AllowsTool` (FridaTaskRouting.cs:86-96), `CanUseTool` (FridaInteractionPolicy.cs:222) | Risco operacional que cresce junto com a autonomia |

### 2. Memória

| Camada | Estado atual | Lacuna |
|---|---|---|
| Conversa | 16 entradas de texto (8 trocas), corte seco | Sem resumo do que saiu; resultados de ferramentas e planos não entram (AgentBrainAndTray.cs:939, 1090) |
| Triagem | 6 mensagens × 2 400 caracteres | Referências antigas se perdem (FridaEconomyRouting.cs:18) |
| Secretária | Lista de notas/tarefas/lembretes, só quando o modelo chama `secretary_memory`; prompt recebe os 12 itens abertos mais recentes | Não é memória de perfil, preferências, correções ou fatos do ambiente (FridaConfig.cs:403-520) |
| Projeto | Último pedido/resposta de até 8 conversas, 4 000 caracteres | Sem resumo de decisões nem do estado do projeto (FridaConversationStore.cs:247) |
| Episódica | Reuniões, relatórios de decisão e recibos são gravados | Nunca voltam ao contexto: `LoadMeetings` só é usado em testes; `Executions/*.json` só na tela de detalhes |
| Recuperação | — | Não há busca (palavra-chave ou semântica) sobre nada do que foi vivido |
| Equipe | — | Membros não recebem memória nem contexto de projeto |
| Lições de skill | Gravadas no `SKILL.md` | Nenhum modelo lê: o prompt só leva nome, descrição e caminho (FridaSkillSystem.cs:424) |

### 3. Skills

- Só existe **1 skill** em disco (`skills/consolidar-placas-pendentes-...`).
- Skill = um script + `SKILL.md` gerado. O `SKILL.md` é saída, não entrada: não há skill de instruções (procedimento, método, referência).
- `FindMatching` é código morto; a ativação real é despejar o catálogo inteiro no prompt de toda requisição.
- Gatilho de evolução por palavra-chave: "aprenda", "ensine", "descubra como" → pipeline de criação de skill (FridaTaskRouting.cs:127). "Me ensine inglês" cria skill.
- `ClassifyComplexity` contém `com\b` — a preposição "com" classifica quase tudo como complexidade alta (FridaSkillSystem.cs:453).
- A "pesquisa" do Estagiário na evolução é o modelo respondendo de memória; não há busca web real (FridaSkillSystem.cs:562-576).
- O Técnico escreve o script em uma única resposta JSON, sem ferramentas e sem rodar nada.
- Verificação fraca: padrão `Write-Output 'OK'`; código de saída 0 já vale como verificado.
- Falsas conclusões: o resumo diz "✅ Nova Skill Aprendida e Verificada" mesmo quando o teste falhou (FridaSkillSystem.cs:654); `manage_skills learn` devolve recibo `Verified` sempre (AgentBrainAndTray.cs:5559-5561).
- Autoaprendizado roda em **toda** reunião: `!IsNullOrWhiteSpace(sources)` é sempre verdadeiro porque `sources` começa com um texto padrão (FridaTeam.cs:581, 637) — gasta uma chamada do Gestor e pode registrar skill inútil a partir de 1 000 caracteres de relato.
- Execução concatena argumentos numa linha de PowerShell (FridaSkillSystem.cs:357-361); timeout fixo de 60 s; sem histórico de versões do script nem rollback.
- Conhecimento fixo (ler PDF/Excel, compilar C# com csc, cruzar planilhas e WhatsApp) está repetido em três prompts (BuildSystemPrompt, prompt do Gestor, prompt de execução) em vez de ser skill.

---

## Plano de melhoria

Ordem recomendada: cada fase entrega algo utilizável e testável sozinha.

### Fase 0 — Corrigir falsos positivos e falsas conclusões (rápido, baixo risco)
- `FridaTaskRouting.cs`: evolução de skill só com intenção explícita sobre skills (remover "aprenda/ensine/descubra como" isolados).
- `FridaSkillSystem.cs`: tirar `com\b`; título do resumo conforme `passed`; rejeitar comando de verificação trivial; não marcar `Verified` por código de saída 0 sem asserção.
- `AgentBrainAndTray.cs:5559`: recibo de `learn` reflete o resultado real da verificação.
- `FridaTeam.cs:637`: autoaprendizado só quando houve pesquisa real ou novo procedimento **e** o veredito foi Aprovado (mover para depois da decisão).
- Testes novos em `tests/FunctionalTests.cs` e `tests/WorkspaceTests.cs` para cada item.

### Fase 1 — Entender pelo modelo, não pela palavra (planejador)
- Novo `FridaPlanner.cs`: a triagem atual (`QueryEconomyAsync`) passa a devolver um plano estruturado — `objetivo`, `tipo`, `precisa_ferramentas`, `precisa_web`, `precisa_dados_locais`, `passos[]` (cada um com papel sugerido), `skills_candidatas`, `pergunta_de_esclarecimento`. Recebe histórico resumido, índice de memória e índice de skills.
- `FridaTaskPlan` ganha `Steps`; `FridaEconomyRouting.Apply` passa a confiar no plano.
- Regex continua apenas como: (a) atalhos determinísticos — abrir programa, comandos nativos de energia, WhatsApp; (b) travas de segurança já aprendidas (`NeedsFreshData` nunca responde direto); (c) reserva quando nenhum provedor responde.
- Orçamento da triagem: mais tokens/tempo quando o pedido é longo.

### Fase 2 — Hierarquia real: orquestrador com delegação
- Novo `FridaModelCatalog.cs` + bloco em `config.example.json`: por modelo — pontos fortes (código, raciocínio, visão, web, velocidade), janela de contexto, suporte a ferramentas, custo, reserva. Papéis são resolvidos pelo catálogo; some a duplicação de fallback em `QueryCoreAsync`.
- `ExecuteTechnicalRequestAsync(instrução, papel)`: o loop de ferramentas usa primeiro a rota do papel pedido (Kimi para código, DeepSeek R1 para análise/cruzamento, Gemini para web/tela, Groq/Cerebras para respostas rápidas). O painel passa a mostrar quem de fato trabalhou.
- Gestor ganha loop próprio com ferramentas de leitura + `delegate_task(papel, instrução, ferramentas)`; subagentes rodam com contexto isolado e devolvem resumo + recibos. O roteiro fixo de `RunAsync` vira o fluxo que o Gestor monta; pesquisas independentes rodam em paralelo.
- Permanece obrigatório (ponto forte atual): auditoria do Analista com base nos recibos, ciclos de correção, decisão estruturada e relatório da Secretária. O Analista ganha ferramentas de leitura e `run_project_validation` para conferir por conta própria.
- Remover os regex internos (`needsResearch`, `needsSeniorResearch`, `multiAgentOrCross`) em favor do plano.

### Fase 3 — Web de verdade
- Nova ferramenta `fetch_url` (GET + HTML→texto, limite de tamanho, recibo com URL e SHA-256) e `web_search` devolvendo lista título/URL/trecho, separada da resposta sintetizada.
- Laço de pesquisa: buscar → ler páginas → refinar consulta → citar `[E#]` (mecanismo de referências já existe em `_requestReferences`).
- Conteúdo de página entra como dado não confiável (mesmo tratamento de `untrusted_output` dos recibos); `IsSafeWebSearchQuery` continua barrando nomes internos.
- Pesquisa profunda: consultas geradas depois de ler resultados, em subagentes paralelos.

### Fase 4 — Memória em camadas
- **Trabalho**: compactar turnos antigos em resumo em vez de descartar; guardar um extrato dos recibos de cada turno (`FridaConversationBrain.cs`, `AgentBrainAndTray.cs:939/1090`).
- **Longo prazo**: novo `FridaMemoryStore.cs` — um arquivo por memória com tipo (perfil do operador, preferência, correção recebida, fato do ambiente, decisão de projeto), origem (conversa, data) e índice de uma linha sempre no prompt; corpo sob demanda pela ferramenta `memory` (buscar, ler, gravar, atualizar, esquecer). Busca por palavra-chave ponderada primeiro; embeddings ficam como evolução.
- **Extração automática**: ao fim de cada pedido, o Estagiário propõe o que guardar; correções do operador viram memória de "como trabalhar".
- **Episódica**: indexar relatórios de decisão e reuniões (já gravados por `FridaIdeRuntime.SaveMeeting` e `FridaDecisionReports`) e expor `recall_history`; `ProjectMemory` vira resumo contínuo por projeto.
- **Equipe**: `Ask()` passa a incluir memória relevante e contexto de projeto.
- **Interface**: tela de memória na IDE para ver, editar e apagar — também atende a auditoria de LGPD que ficou adiada.

### Fase 5 — Skills v2
- Formato: `SKILL.md` com cabeçalho (nome, descrição, quando usar, ferramentas, papel indicado) + corpo de instruções + `scripts/` e `references/` opcionais. Skill só de instruções passa a ser válida.
- Divulgação progressiva: o prompt leva só nome + descrição; nova operação `manage_skills read` carrega corpo e lições quando o modelo escolhe a skill. `FindMatching` vira apenas dica para o planejador.
- Criação pelo loop de agente: o Técnico escreve, executa e testa com as ferramentas reais, usando a pesquisa web da Fase 3. Estados: rascunho → testada → aprovada, com relato honesto.
- Registro de skill executável pede aprovação do operador (cartão no chat); lições continuam automáticas após veredito Aprovado.
- Execução segura: argumentos em arquivo JSON/array, timeout por skill, versão anterior do script preservada e rollback se a verificação falhar após atualização.
- Métricas no catálogo: usos, taxa de sucesso, último uso; skill parada é sinalizada.
- Skills semente: mover para skills o conhecimento hoje repetido nos prompts (leitura de documentos, compilação C# com csc, cruzamento planilhas × WhatsApp, método de pesquisa) — encurta o prompt de sistema.

### Fase 6 — Travas e avaliação
- Portão de confirmação configurável para ações destrutivas/irreversíveis e para registrar skill executável, em `FridaTaskPlan.AllowsTool` e `FridaInteractionPolicy.CanUseTool`.
- Bateria "sem palavra-chave" em `tests/`: 40 pedidos parafraseados (ex.: "tô sem espaço no C:", "o que mudou na norma de soldagem este ano?") medindo rota correta, uso de web, uso de skill, uso de memória e veredito — nos moldes da bateria de código 30/30.

---

## Arquivos principais

- Alterar: `FridaTaskRouting.cs`, `FridaEconomyRouting.cs`, `FridaTeam.cs`, `FridaSkillSystem.cs`, `AgentBrainAndTray.cs` (BuildSystemPrompt, GetToolDeclarations, despacho de ferramentas, loops de execução), `FridaConversationBrain.cs`, `FridaConversationStore.cs`, `FridaConfig.cs`, `config.example.json`, `compilar_frida.bat`, `compilar_frida_agy.bat`, `tests/run_tests.ps1`.
- Criar: `FridaPlanner.cs`, `FridaModelCatalog.cs`, `FridaMemoryStore.cs`, `FridaWebTools.cs`, testes correspondentes.
- Reaproveitar: `FridaToolReceipt` / `FridaExecutionLedger` (recibos), `FridaVerifiedTools.RunPowerShell` e `WriteFile`, `FridaDecisionReports`, `FridaContextPaginator`, `_requestReferences` (citações), `ModelQuotaGuard` (cota e cooldown), `FridaDocumentIO.ReadText`.

## Forma de trabalho

Antigravity/Codex editam `Agente Frida/` ao vivo: trabalhar em cópia isolada (`scratch/`) e fazer merge de 3 vias no fim, normalizando finais de linha. Uma versão instalada por fase, com backup de rollback, via `tests/install_release.ps1 -TargetDirectory "Agente Frida"`.

## Verificação

- A cada fase: `tests/run_tests.ps1` (C# 232/232), bateria de código 30/30 e testes Python (79) sem regressão; testes novos da fase aprovados.
- Fase 1: bateria "sem palavra-chave" ≥ 90 % de rota correta com o planejador e reserva por regex funcionando com provedores desligados.
- Fase 2: registro de execução mostra o modelo do papel pedido em cada delegação; pedido de equipe simples usa menos chamadas que hoje.
- Fase 3: pedido "pesquise como fazer X e faça" produz recibos de `web_search` + `fetch_url` com URLs citadas no relatório.
- Fase 4: fato dito numa conversa é recuperado em outra conversa após reiniciar o app; apagar na tela remove do prompt.
- Fase 5: criar skill nova de ponta a ponta com teste funcional real; skill com teste falhando aparece como rascunho, nunca como verificada.
- Teste manual no app instalado ao fim de cada fase (chat da IDE e voz).
