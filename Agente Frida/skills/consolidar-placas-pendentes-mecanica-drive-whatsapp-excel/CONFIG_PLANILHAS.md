# CONFIGURAÇÃO DE ACESSO E CONTEXTO DAS PLANILHAS OFICIAIS

Esta nota registra a estrutura e as permissões de acesso às planilhas utilizadas pela Frida para o suporte ao Ailton Soares (dj).

## 1. Referência das Planilhas
As planilhas são armazenadas no Google Drive do Ailton e acessadas via browser.

- **Nome Base:** Projeto e Gestão de Ordem de Serviços
- **ID Principal:** 1sD7RnNjHBCt4TWWZizldaxi20WS7gjVmZKKEn8796PQ (Google Sheets)

### Estrutura de Abas
1. **01- PROJETO CHECK-LIST** (gid=1047389234): Foco em controle de chassi e datas de recebimento.
2. **GESTÃO PCP - equipe**: Controle de status de produção e fluxo de oficina.
3. **ORDENS DE SERVIÇOS COMERCIAL - CONTROLE**: Controle financeiro e comercial de ordens de serviço.

## 2. Protocolo de Acesso e Segurança
- **Status de Acesso:** Consumidas via ferramentas de leitura do agente (somente leitura).
- **Autorização:** Necessário nível "Editor" para o e-mail do operador/conta de serviço para que a automação via API possa realizar a leitura dos metadados e células.
- **Integração:** Os dados destas planilhas servem de base para o cruzamento com o histórico de mensagens do WhatsApp (via `read_whatsapp`) para identificar pendências de mecânica.

## 3. Skill de Consolidação
A skill `consolidar-placas-pendentes-mecanica-drive-whatsapp-excel` utiliza estas fontes para gerar um relatório unificado de pendências (ID_PLACA, STATUS_MECANICA, ORIGEM, PRIORIDADE).
