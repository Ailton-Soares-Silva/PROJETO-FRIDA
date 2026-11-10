# Cruzamento Planilhas Drive x WhatsApp

## Descrição
Procedimento analítico para relacionar registros operacionais das 3 planilhas do Google Drive do operador (chassis, ordens de serviço, peças, corte e dobra) com mensagens, solicitações e pedidos localizados no WhatsApp.

## Metadados Operacionais
- **ID**: `cruzamento_planilhas_whatsapp`
- **Complexidade**: `High`
- **Agentes Responsáveis**: `SeniorResearcher`, `Analyst`
- **Modo de Execução**: `instructional`
- **Status de Verificação**: ✅ Comprovado e Aprovado
- **Data de Criação**: 2026-11-10 09:30:00 UTC
- **Versão**: 1

## Padrões de Ativação
- `cruzar dados da planilha com whatsapp`
- `verificar chassi no grupo e na planilha`
- `status do pedido no corte e dobra`
- `chassis atrasados e mensagens de cobranca`

## Instruções Operacionais
1. Consulta Estrita de Somente Leitura nas 3 Planilhas do Drive:
   - Utilizar a biblioteca `FridaBaseSpreadsheets` para inspecionar registros, colunas e identificadores.
   - NUNCA modificar células ou salvar alterações nas planilhas base.
2. Consulta no WhatsApp:
   - Utilizar `read_whatsapp` com identificador exato (número de chassi, código de peça ou nome da empresa/cliente).
   - Verificar mensagens relevantes nos grupos técnicos (`Corte e Dobra`, `Montagem`, etc.).
3. Cruzamento Analítico:
   - Confrontar status do chassi/ordem na planilha com o último relato no grupo do WhatsApp.
   - Apontar divergências de datas, pendências de peças ou confirmações de entrega sem criar fatos não comprovados.
