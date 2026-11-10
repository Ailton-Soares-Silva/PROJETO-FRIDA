# Frida — Matriz de Capacidades, Hashes de Referência e Homologação (`v1.3.0.0` — 2026-11-05)

## 1. Consolidação da Versão `v1.3.0.0` (Chat IDE Frida + Copiloto de Programação Autônoma)

- **Diretório do projeto:** `c:\Users\Ailton Soares\Documents\print window mbx\PROJETO FRIDA\Agente Frida`
- **Pacote de release reproduzível:** `scratch\release-ide-1.3.0.0\package.json`
- **Manifesto de restauração (rollback atômico):** `scratch\release-ide-1.3.0.0\backup-20261105-121105-6c4d6bfd\rollback.json`
- **Backup de restauração local anterior:** `FridaAgent.restore_backup.exe` (`SHA-256: FDF57A79FC099F0D6E204B08471AAC033C70765C09B4D3530582A58C5DC568A3`)
- **Executável ativo instalado em produção (`FridaAgent.exe` `v1.3.0.0`):** `SHA-256: E21ED00353B33E2A4348934274A3DE494E463BABDB52EAF0A327BCB259E1507B` (PID ativo `36588`, `version: "1.3.0.0"`, `ui_stale: false`)

### Hashes SHA-256 do Pacote e das Fontes Consolidadas (`v1.3.0.0`)

| Arquivo | SHA-256 |
|---|---|
| `FridaAgent.exe` (`v1.3.0.0`) | `E21ED00353B33E2A4348934274A3DE494E463BABDB52EAF0A327BCB259E1507B` |
| `AgentBrainAndTray.cs` | `5C6E5522596901BCE0128B8226937E612AFB22E01E79B69E6F404B7116FE2F85` |
| `AudioVoiceEngine.cs` | `F867B2F40990259813F15B8A49722AFDE3ECAEB6888C38F6BE265E9F1B343B02` |
| `FridaCodeEditor.cs` | `90AF390345969A336CD67167976D789D2B32D1C62B8C8B60F7327C25BB8953AC` |
| `FridaCodingCopilot.cs` | `D75D51C4EC358D74514A2D3EE4C4CBFE94ABF61C4824547FA41AAE63CF40575E` |
| `FridaConfig.cs` | `2D0B356693A96848B5DD1EC9BA23D691EDD98C00E9D1C9A4F22811CEAC105458` |
| `FridaConversationAudit.cs` | `1A966072014B434A4EAD95D78C4EFD4DB4AF196C3B56F4FBFB7DF9435F6ABE38` |
| `FridaDocumentIO.cs` | `9C5C398AEC4316FAC43B66EC7AEC36D8A086A40B02BE2F186A14685F8963ECAC` |
| `FridaEconomyRouting.cs` | `F62F80E5C0D2B4A1C92AD02941A8C6BFCC838351066B9C2BCB20F9FB44C91CD3` |
| `FridaExecutionProtocol.cs` | `31C81BB56201074AB9231FC9E3259C1602B3F45F28A7F560CEDD163668A8A49F` |
| `FridaFileSearch.cs` | `8A43AE6F70D06FA344038E6D7D15B0E45AB9E0690F871FA1643B27D46A08CB68` |
| `FridaIdeForm.cs` | `5EF73DBE0F7A5D895E9B63C18D712C0E3558045058923DD89FF3D6175F44A7BC` |
| `FridaIdeRuntime.cs` | `3AFA4A422392F81D99BAACA88FBE62B70DE033DA5DDC7DF05AABDF10B221EBF3` |
| `FridaInteractionPolicy.cs` | `3E37DC6F197EFD1FF082D45046DD7E8993592BC208F38F4B5BB7DBFFD357D78C` |
| `FridaOperational.cs` | `277CA4B8E9B5B07CACE91762E708ED78D34EA6CF8D5E57C18B1FF1CC6164D552` |
| `FridaPrivateSources.cs` | `C5410713D36C0D1D7F0E719195D0E18A553DBE33FE24DE8C9F071F550CB19F56` |
| `FridaReliability.cs` | `9E8379B4564D0E470D0341A0C73E11666365D698E4E142C9F0662DE955F100A6` |
| `FridaResultForm.cs` | `363D9958BCDBBCD2AC3323E347A4528198D269E58BA6CF6E0784C1F81FF27961` |
| `FridaRuntimeHealth.cs` | `2B89FAA3EF3C845AB51759E48B89DAA56E1B23F3DD86A43C7FA090B112CCAE9B` |
| `FridaSessionControl.cs` | `9BEA3FB814C5E27AA4864BDAE53D90C2C2777E0BE18645DEAF7D7ED15811F678` |
| `FridaSshWorkspace.cs` | `8DCE57D21E121A4BC5E31A447D859EFF62DAF1AF1B0C3E7F1B013ACF3C7BC4E0` |
| `FridaTaskRouting.cs` | `4C63E0B3A3FAFBFDC482829FEA1C7709DB3D32DFDC81DBCEA367DF6C0656B4E3` |
| `FridaTeam.cs` | `7FF9398ECD6FE5ACFA09FECA8C1166833C097ACC170265314BB3325438550130` |
| `FridaVerifiedTools.cs` | `A6C048B073957971BAFFFA918F8DA3A28528BD977C743D5858005B0BC891F0E9` |
| `FridaWhatsAppResearch.cs` | `EC3FD493B391DDE1E9D737AC0C1109471008F7881B05BE5AAD7E029752E756DB` |
| `FridaWhatsAppWorker.cs` | `060C560C7F458454C751E92F123926AE3C465E70105C9D6798C19B4A84F47E2F` |
| `HologramOrbForm.cs` | `EB70E5FEC005A94A840D87F208FA46A2CAB6751283AB805D9048CDA2006FEAE1` |
| `LlmUsageLedger.cs` | `13DEBF81711693BA08EA185F7B5E80F880A25E09E18E4D60E4897FEC713210CF` |
| `LocalWakeDetector.cs` | `7F568C1CCBBBE0467D7D03B5A89B749EDFEF1BD4BED21BD00AF36FEDBB473F1C` |
| `SherpaVoiceNative.cs` | `B2EB1C0742CFCC235CB012DBFF2BF3C63549E14019D45D50595963226CD30D19` |
| `SpeakerVoiceProfile.cs` | `E9E8B25CA3E60ADB268E53398FCDE5A28D4C30519FE006B55799C24B2ECE90A0` |
| `VoiceEnrollmentForm.cs` | `861025559529FF379BEEA9D6C6D156F4073211901D0D43CB9C3EC3F9CF9C88A1` |
| `VoiceEnrollmentSession.cs` | `35A7680038E28187D42A69B73FCB8498431649EE2CFF10444223071BAB6CDF6A` |
| `ZeldaConsumptionViews.cs` | `17F9C576C9272756F43971455403FCCE6BD226347A22852B6BD370231466F2B1` |
| `ZeldaExecutiveLlmDashboard.cs` | `69813E2105883AA7224415D60B2CC791506D5424CCD39E5E02DC01C7DEA1D26F` |
| `whatsapp_reader.py` | `085E1CDB81805AB46AA552DC821D8D0F1B7A6F47C2BED018C72A0D1BB9EDA0B5` |
| `whatsapp_runtime.py` | `48C2553AC49630ED856392747A02243CC5E5C228DD170EAA1CCE6C1B568EBB48` |
| `whatsapp_dom.py` | `E290A759A83C17F470FD7B369596824CA37AC9C0049218AE60CD4AB531FAB8B6` |

---

## 2. Matriz de Capacidades (`v1.3.0.0`)

| Capacidade | Implementada | Testada | Disponível na Máquina | Pendente | Evidência Técnica |
|---|---|---|---|---|---|
| **Chat IDE Frida (`FridaIdeForm`) integrada ao Tray e atalhos (`Ctrl+Alt+F`, `Ctrl+Alt+L`, `F4`)** | Sim | Sim | Sim | Não | `FridaIdeForm.cs` + `AgentBrainAndTray.cs` + `IdeTests.cs` |
| **Chat silencioso compartilhando `FridaBrain` com a voz + gravação revisável do microfone** | Sim | Sim | Sim | Validação física com fala humana ao vivo (`physical_voice`) | `AudioVoiceEngine.BeginIdeRecording` / `FinishIdeRecording` + `IdeTests.cs` |
| **Pareamento WhatsApp com QR Code embarcado em memória via worker JSONL** | Sim | Sim | Sim | Leitura física com câmera do celular quando necessário (`phone_pairing`) | `FridaWhatsAppWorker.cs` + `whatsapp_reader.py` + `test_whatsapp_runtime.py` |
| **Projetos locais e remotos (SSH/SFTP VPS) com editor `FridaCodeEditor` e reconciliação** | Sim | Sim | Sim | Escolha do host/alias VPS pelo operador (`live_ssh`) | `FridaSshWorkspace.cs` + `FridaCodeEditor.cs` + `IdeTests.cs` |
| **Terminal em tempo real com eventos clicáveis de arquivos (`_terminalFiles` → aba `Código`)** | Sim | Sim | Sim | Não | `FridaIdeForm.cs` + `FridaIdeRuntime.cs` + `IdeTests.cs` (#16) |
| **Histórico paginado com filtros e vínculos diretos (`Ver execução`, `Arquivos do atendimento`, `Ver reunião`, `Ouvir áudio`)** | Sim | Sim | Sim | Não | `FridaIdeForm.MakeHistory` + `IdeTests.cs` |
| **Separação de estados de entrega** (`file_saved`, `process_completed`, `build_passed`, `tests_passed`, `request_fulfilled`) | Sim | Sim | Sim | Não | `FridaExecutionLedger` + `FridaDevelopmentTaskState` + regressão P1 #1 |
| **Vinculação de validação à revisão do código** (edição invalida compilação/teste anterior) | Sim | Sim | Sim | Não | `FridaExecutionLedger.Add` (`CurrentProjectRevision`) + regressão P1 #2 |
| **Cache de ferramentas vinculado à revisão e estado** | Sim | Sim | Sim | Não | `AgentBrainAndTray.ToolCallIdentity` + regressão P1 #3 |
| **Paginação estruturada de contexto preservando JSON válido** | Sim | Sim | Sim | Não | `FridaContextPaginator.PaginateToolMessageContent` + regressão P1 #4 |
| **Recuperação em cascata Groq → Cerebras → Gemini** | Sim | Sim | Sim | Não | `FridaProviderBudget.ReleaseFailedAttempt` + regressão P1 #5 |
| **Ciclo obrigatório Revisor → Técnico** (até 3 ciclos de correção) | Sim | Sim | Sim | Não | `FridaTeamCoordinator.RunAsync` + regressão P1 #6 + Desafio #10 |
| **Raciocínio dinâmico (`low`/`medium`/`high`) e orçamento de tokens (`8192`/`16384`)** | Sim | Sim | Sim | Não | `FridaTaskRouter` + `FridaTeamClient` + regressão P2 #7 |
| **Timeout real do FFmpeg com kill de árvore de processos** | Sim | Sim | Sim | Não | `AudioVoiceEngine.ConvertMp3ToWavViaFfmpegAsync` + regressão P2 #8 |
| **Instalador atômico com manifesto de rollback e proteção de `config.json`** | Sim | Sim | Sim | Não | `tests/prepare_release.ps1` + `tests/install_release.ps1` + `tests/test_release_install.ps1` |

---

## 3. Resultados da Suíte Unificada (`v1.3.0.0`)

- **Suíte C# (`FunctionalTests.exe`):** **150 / 150 aprovados (`failed=0`)**
  - 79 testes funcionais e operacionais originais
  - 45 testes de qualificação e Copiloto WhatsApp
  - 9 testes de regressão P1/P2 e casos de borda do Copiloto de Programação
  - 1 bateria agregada de programação autônoma (30 execuções consecutivas: **30/30 resolvidos, 0 falsos sucessos**)
  - 16 testes de integração da Chat IDE Frida (`IdeTests.cs`, incluindo streaming em tempo real no terminal, gravação/cancelamento de microfone, validação e reconciliação SSH/SFTP, realocação de artefatos, arquivo de reuniões, ocultação de credenciais e abertura de arquivo no editor `Código` a partir de evento clicável no `Terminal`)
- **Suíte Python (`unittest discover`):** **65 / 65 aprovados (`OK` em `26.88s`)**
- **Qualificação do Instalador e Rollback (`tests/test_release_install.ps1`):** **100% aprovado** (`install_verified: true`, `rollback_verified: true`, `configuration_preserved: true`, `tamper_rejected: true`, `partial_failure_rolled_back: true`)
