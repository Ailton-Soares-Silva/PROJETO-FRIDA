# Intervenção WhatsApp — 4 de novembro de 2026

## Estado da entrega

**Homologada e instalada: FridaAgent 1.2.0.0.** A bateria real de 30 consultas atingiu todos os critérios do plano. O executável foi substituído com backup verificado, e a Frida foi iniciada e está em espera (`inativa`, UI respondendo, microfone ativo).

- SHA-256 instalado: `65cb47565c6f4ef021e2b71481c469b5739f44e636aef6eae02e6229990a2431`
- Anterior (1.1.0.0): `92ce86e2afc721ed94c4df8deb2c2bc8f5066a74aab4197d20fe2896cd8d098e`, preservado em `scratch/release-1.2.0.0/FridaAgent-1.1.0.0-backup.exe` e no baseline.
- Manifesto: `tests/results/release-manifest.json`.

### Correção posterior (22h30) — versão 1.2.1.0

Na 1.2.0.0, toda busca respondia que o WhatsApp "ainda está sincronizando com o celular". **O diagnóstico estava errado.** O rodapé "Sincronizando mensagens mais antigas · Ver progresso" é **fixo** em aparelhos conectados. O "Ver progresso" não traz porcentagem, apenas orienta: "Mantenha o WhatsApp aberto no seu celular… Para acessar o histórico completo da conversa, abra o WhatsApp no seu celular". O aviso indica que parte do histórico fica só no celular, e não uma sincronização em andamento. Além disso, o falso sinal interrompia a busca na conversa antes de os resultados carregarem.

- O rodapé deixou de contar. Só um indicador real de sincronização no painel lateral torna a busca parcial.
- Uma busca vazia dentro da conversa é conferida no histórico carregado antes de confirmar a ausência.
- Quando a busca tem **um** identificador e palavras descritivas ("caixa número 2377"), a Frida busca só o identificador (`2377`), porque o WhatsApp exige todas as palavras.
- Sem resultados, a resposta informa o escopo: "a busca cobre o histórico disponível no WhatsApp Web".

Verificação com os pedidos reais do operador: "Quantas peças… Qualidade Corte e Dobra" retornou "Quantidade: 19 peças". "Caixa número 2377" retornou "Caixa 2377" (Qualidade Encarroçamento). As duas buscas ficaram completas. **120 testes C# e 62 Python aprovados.** Versão 1.2.1.0 SHA-256 `e6b85baa1a81cb0571389480d2dfb5c88708967db1379a6b28c5c6a7d10e2300`, instalada por `tests/install_release.ps1` quando a Frida é fechada pelo menu Sair.

## Histórico

O agente anterior (Codex) implementou a rota sênior, o worker JSONL, o SQLite v2 e o adaptador de interface. Ele foi interrompido três vezes por limite de uso. A última interrupção ocorreu às 20h24, com um patch não testado da busca dentro da conversa e o teste de contatos falhando. Esta etapa retomou o trabalho a partir desse ponto e o concluiu.

## Corrigido nesta etapa

| Problema encontrado | Efeito antes | Correção |
|---|---|---|
| Pedido C#→Python com BOM (`Console.InputEncoding` UTF-8) ou em cp1252 | **No atendimento real, o worker não respondia** ("Prazo excedido na etapa worker_start"). Acentos derrubavam a leitura do stdin | O C# grava UTF-8 sem BOM. O Python decodifica cada linha (`utf-8-sig`) dentro do `try`. `protocol_error` falha imediatamente |
| Identidade da conversa ancorada em uma mensagem renderizada | `chat_id` mudava com a rolagem, quebrando deduplicação e retomada | `ui:` + hash(nome\|tipo[\|discriminador]). Migração do cache para v3 com backup |
| Resultado de mensagem contado como conversa; "Grupos em comum" | Homônimos falsos e opções duplicadas | Só a seção "Conversas" (ou "Contatos") conta |
| Nome parcial ("custos de reforma") | `not_found` | Correspondência única parcial é aceita; várias geram uma lista numerada |
| Escolha entre conversas | Inexistente | `chat_candidate`. "2", "o segundo" e "opção 3" são resolvidos **sem modelo**. Homônimos reais usam o telefone ou o criador do grupo no painel de dados |
| Tipo da conversa pelo subtítulo | Grupos "Somente admins", contas comerciais e "Mensagens para mim" falhavam | Tipo lido dos botões de ligação ("…em grupo", "Ligação de voz") |
| Emoji no nome (`<img alt>`) | `chat_mismatch` | O nome inclui o `alt` |
| Cursor gravado antes de resolver a conversa | "continue" numa conversa sempre dava `invalid_cursor` | Fingerprint usa o `chat_id` resolvido. "continue" é determinístico, sem consultar o modelo |
| Popover de pesquisas recentes | `ElementClickInterceptedException` (onde o agente parou) | Esc no campo, filtro já selecionado não é clicado de novo, clique JS como alternativa |
| Direção por "Você:" (só na 1ª bolha) | 13 mensagens próprias marcadas como recebidas | Recibo de leitura ("Lida/Entregue/Enviada") e `tail-out` |
| Legendas de PDF/imagem/vídeo sem metadados | Ignoradas, 20 s de espera e nova tentativa a cada página (89 s) | Data pelo divisor ("Hoje/Ontem/sexta-feira/dd/mm/aaaa") mais o horário. Falha registrada uma vez como `unextractable_hit` |
| Sincronização de histórico em andamento | Busca dentro da conversa dizia "Nenhuma mensagem"; busca global podia omitir mensagens carregadas e se declarar completa | Estado lido no painel de busca (cache de 5 min). Global: `partial/history_sync_in_progress`. Conversa: leitura do histórico carregado. Sem `confirmed_empty` |
| Resumo em continuações ("o segundo", "e ontem?") | O modelo recebia só "o segundo" e não validava | A síntese recebe o pedido de origem, o complemento e a busca executada |
| Fala | Lia códigos `[M…]` e a linha técnica de cobertura | Removidos da fala; continuam na tela |

## Evidências

### Testes automatizados
**112 testes C# e 61 testes Python aprovados** (`tests/run_tests.ps1`). Incluem o fluxo completo ambiguidade → escolha → resumo → continue, a fala, a sincronização e o BOM do .NET.

### Atendimento real de ponta a ponta
`tests/run_whatsapp_e2e.ps1` usa o cérebro de produção, o `config.json` (somente leitura), os modelos qualificados e o worker com o perfil autenticado:

| Pedido | Resultado |
|---|---|
| "pesquise nos grupos do whatsapp … sobre o 3213" | Mensagem de "Custos de Reforma - Dono Paulo" com resumo validado, 27 s |
| "busque 3213 na conversa custos de reforma" | Mesmo registro (mesma referência), 15 s |
| "procure … obrigado na conversa PCP" | Lista de 7 conversas numeradas, 5 s |
| "o segundo" | Mônica PCP, "Obrigado" (03/11 08:55), sem modelo de planejamento |
| "e ontem?" | Mantém a conversa e aplica 03/11, 14 s |
| termo inexistente | Nenhum registro; ausência não confirmada durante a sincronização |

### Homologação real (`tests/homologate_whatsapp.py`, 4/11 21h57–22h04)
Gabarito com 140 mensagens de 16 conversas reais (12 grupos e 4 contatos), incluindo três páginas antigas por conversa.

| Critério do plano | Meta | Resultado |
|---|---|---|
| Mensagens-alvo recuperadas | ≥ 95% | **30/30**: global 18/18, conversa 8/8, período 4/4, páginas antigas 11/11 |
| Atribuição errada / parcial como completa | 0 | **0 / 0** |
| Progresso / primeiros resultados p95 | ≤ 2 s / ≤ 30 s | **16 ms / 4,0 s** (p50 1,7 s; total p95 8,4 s) |
| Controles negativos (período excludente, ausência) | todos | **4/4** |
| Reinícios mantendo a sessão e retomando | 3 | **3/3**; cursor retomado em novo processo (página 3 → 7, 0 repetidos) |
| Direção e datas | — | 0 inconsistências em 140; 140/140 datadas |

Os resultados detalhados ficam em `tests/results/whatsapp-homologation.json` e `whatsapp-e2e.json`. Esses arquivos contêm mensagens reais e estão no `.gitignore`.

## Limitações conhecidas

1. **Histórico do aparelho conectado:** o WhatsApp Web não guarda todo o histórico do celular, e mensagens muito antigas podem não aparecer. A busca é **literal**: "peças" não encontra "PÇs". Perguntas de soma ("quantas peças foram entregues") retornam as mensagens encontradas, sem o total somado.
2. Em resultados **globais** de conversas homônimas, a origem não é distinguida. A Frida avisa ("N conversas se chamam…"). Na busca direcionada, os homônimos são resolvidos por escolha numerada.
3. Em legendas de mídia **recebidas em grupo**, o remetente aparece como "remetente não identificado", porque o WhatsApp não o expõe.
4. Fora do escopo acordado: conteúdo de anexos e áudios, envio de mensagens e auditoria de LGPD e ética.
5. A reprodução sonora real e a estabilidade de duas horas não foram reexecutadas nesta etapa. O texto falado foi verificado, e o motor de voz não foi alterado.

## Reproduzir e reverter

Na pasta `Agente Frida`:

```powershell
.\tests\run_tests.ps1                 # 112 C# + 61 Python
.\tests\run_whatsapp_e2e.ps1          # atendimento real (chamadas aos provedores)
python tests\homologate_whatsapp.py   # bateria real de 30 consultas + reinícios
python whatsapp_reader.py --search-live 3213 --chat-name "custos de reforma"
```

Reversão completa da intervenção: encerre a Frida pelo menu **Sair** da bandeja e execute `tests\restore_whatsapp.ps1`. O script restaura o executável 1.1.0.0, os fontes e o leitor do baseline, preservando perfil, cache e credenciais. A candidata antiga `scratch/whatsapp-candidate/` foi superada por esta versão.
