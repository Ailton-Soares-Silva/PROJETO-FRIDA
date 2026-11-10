// Testes de Avaliacao Sem Palavras-Chave (40 cenarios)
// Valida a capacidade do FridaPlanner e FridaTaskRouting de inferir intencoes reais
// a partir de linguagem natural e coloquial sem exigir termos tecnicos especificos.

using System;
using System.Collections.Generic;

namespace FridaAgent
{
    internal static class KeywordFreeTests
    {
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static void Run(Action<string, Action> test)
        {
            // Grupo 1: Operações de Sistema / Espaço / Hardware (8 casos)
            test("sem palavra-chave 01: espaco em disco", () =>
            {
                var plan = FridaTaskRouting.Decide("como ta o espaco no disco principal?", false, false);
                Check(plan.Kind != FridaTaskKind.Conversation, "Nao identificou consulta de sistema");
            });

            test("sem palavra-chave 02: memoria e processos", () =>
            {
                var plan = FridaTaskRouting.Decide("tem algum processo consumindo muita memoria agora?", false, false);
                Check(plan.Kind != FridaTaskKind.Conversation, "Nao identificou inspecao de processos");
            });

            test("sem palavra-chave 03: modelo de hardware", () =>
            {
                var intent = FridaRequestIntent.Classify("qual o modelo do processador dessa maquina?");
                Check(intent.RequiresTools, "Inspecao de hardware tratada como conversa");
            });

            test("sem palavra-chave 04: reinicio agendado", () =>
            {
                int sec;
                Check(FridaDuration.TryParse("quarenta minutos", out sec) && sec == 2400, "Duracao falhou");
            });

            test("sem palavra-chave 05: cancelamento nativo", () =>
            {
                var r = FridaNativeCommands.Execute("cancele o desligamento agendado", (exe, args) => null);
                Check(r != null && r.Operation == "cancel", "Nao reconheceu cancelamento nativo");
            });

            test("sem palavra-chave 06: pendrive conectado", () =>
            {
                var intent = FridaRequestIntent.Classify("da uma olhada se tem pendrive conectado");
                Check(intent.RequiresTools, "Verificacao de dispositivo externo ignorada");
            });

            test("sem palavra-chave 07: ip da maquina", () =>
            {
                var intent = FridaRequestIntent.Classify("qual o endereco ip desta maquina?");
                Check(intent.RequiresTools, "Consulta de rede local tratada como conversa");
            });

            test("sem palavra-chave 08: servicos parados", () =>
            {
                var plan = FridaTaskRouting.Decide("mostre os servicos que estao parados", false, false);
                Check(plan.Kind != FridaTaskKind.Conversation, "Inspecao de servicos caiu em conversa");
            });

            // Grupo 2: Pesquisa Externa / Fatos / Preços / Web (8 casos)
            test("sem palavra-chave 09: cotacao arroba", () =>
            {
                var plan = FridaTaskRouting.Decide("qual o valor da arroba do boi gordo hoje no mercado?", false, false);
                Check(plan.NeedsWeb || plan.ResearchOnly || plan.Kind != FridaTaskKind.Conversation, "Cotacao de mercado nao direcionou para busca");
            });

            test("sem palavra-chave 10: norma tecnica", () =>
            {
                var plan = FridaTaskRouting.Decide("o que mudou na norma de soldagem aws d1.1 recente?", false, false);
                Check(plan.NeedsWeb || plan.ResearchOnly || plan.Kind != FridaTaskKind.Conversation, "Norma tecnica caiu em conversa simples");
            });

            test("sem palavra-chave 11: cambio comercial", () =>
            {
                var plan = FridaTaskRouting.Decide("qual a cotacao da moeda americana hoje?", false, false);
                Check(plan.NeedsWeb || plan.ResearchOnly || plan.Kind != FridaTaskKind.Conversation, "Cotacao cambial nao solicitou busca");
            });

            test("sem palavra-chave 12: previsao do tempo", () =>
            {
                var plan = FridaTaskRouting.Decide("previsao de chuva para ribeirao preto no fim de semana", false, false);
                Check(plan.NeedsWeb || plan.ResearchOnly || plan.Kind != FridaTaskKind.Conversation, "Clima nao direcionou para pesquisa externa");
            });

            test("sem palavra-chave 13: dimensoes de viga", () =>
            {
                var plan = FridaTaskRouting.Decide("quais sao as dimensoes padrao de uma viga i de seis polegadas?", false, false);
                Check(plan.Kind != FridaTaskKind.Conversation || plan.NeedsWeb, "Tabela dimensional de aco tratada como conversa pura");
            });

            test("sem palavra-chave 14: condicoes de rodovia", () =>
            {
                var plan = FridaTaskRouting.Decide("como ta o transito na rodovia anhanguera sentido capital?", false, false);
                Check(plan.NeedsWeb || plan.ResearchOnly || plan.Kind != FridaTaskKind.Conversation, "Transito em rodovia nao acionou pesquisa");
            });

            test("sem palavra-chave 15: bitola de chapa", () =>
            {
                var plan = FridaTaskRouting.Decide("tabela de conversao de bitola msg para milimetros", false, false);
                Check(plan.Kind != FridaTaskKind.Conversation || plan.NeedsWeb, "Tabela de bitolas nao acionou ferramentas/pesquisa");
            });

            test("sem palavra-chave 16: preco medio de materia-prima", () =>
            {
                var plan = FridaTaskRouting.Decide("quanto custa em media o metro da chapa de aco 1020 de 3mm?", false, false);
                Check(plan.NeedsWeb || plan.ResearchOnly || plan.Kind != FridaTaskKind.Conversation, "Preco de material nao acionou busca");
            });

            // Grupo 3: Consulta a Mensagens e Comunicacao (8 casos)
            test("sem palavra-chave 17: recado de corte e chassi", () =>
            {
                var req = FridaWhatsAppRequest.Detect("o que o pessoal do corte falou sobre o chassi 4020?", new KeyValuePair<string, string>[0]);
                Check(req != null, "Nao detectou intencao de pesquisa em conversas de equipe");
            });

            test("sem palavra-chave 18: aviso de frete/chegada", () =>
            {
                var req = FridaWhatsAppRequest.Detect("alguem avisou no grupo quando chega o caminhao de vigas?", new KeyValuePair<string, string>[0]);
                Check(req != null, "Aviso de chegada nao associado a comunicacao");
            });

            test("sem palavra-chave 19: relato de montagem", () =>
            {
                var req = FridaWhatsAppRequest.Detect("o que o time da montagem relatou hoje cedo?", new KeyValuePair<string, string>[0]);
                Check(req != null, "Relato de equipe nao identificado");
            });

            test("sem palavra-chave 20: alteracao de peca", () =>
            {
                var req = FridaWhatsAppRequest.Detect("quem pediu alteracao nas travessas do chassi?", new KeyValuePair<string, string>[0]);
                Check(req != null, "Pedido de alteracao em conversas nao identificado");
            });

            test("sem palavra-chave 21: pendencias e cobrancas", () =>
            {
                var req = FridaWhatsAppRequest.Detect("tem alguma cobranca de peca pendente nas mensagens?", new KeyValuePair<string, string>[0]);
                Check(req != null, "Cobranca pendente nao associada a mensagens");
            });

            test("sem palavra-chave 22: mensagem do serralheiro", () =>
            {
                var req = FridaWhatsAppRequest.Detect("o que o serralheiro mandou sobre as buchas?", new KeyValuePair<string, string>[0]);
                Check(req != null, "Mensagem do serralheiro nao identificada");
            });

            test("sem palavra-chave 23: status de pintura", () =>
            {
                var req = FridaWhatsAppRequest.Detect("qual foi o ultimo recado da equipe sobre a pintura?", new KeyValuePair<string, string>[0]);
                Check(req != null, "Status de pintura nao identificado");
            });

            test("sem palavra-chave 24: confirmacao de recebimento", () =>
            {
                var req = FridaWhatsAppRequest.Detect("alguem confirmou o recebimento das porcas sextavadas?", new KeyValuePair<string, string>[0]);
                Check(req != null, "Confirmacao de recebimento em mensagens ignorada");
            });

            // Grupo 4: Memória e Preferências (8 casos)
            test("sem palavra-chave 25: busca de perfil", () =>
            {
                var results = FridaMemoryStore.Search("Ailton dj operador", 3);
                Check(results.Count > 0, "Nao localizou memoria de perfil do operador");
            });

            test("sem palavra-chave 26: registro de preferencia", () =>
            {
                var entry = FridaMemoryStore.WriteMemory("preference", "Formato de relatórios", "Operador prefere relatórios concisos em tópicos", null, "preferencia,resumo");
                Check(entry != null && entry.Id.StartsWith("mem_"), "Gravacao de preferencia falhou");
            });

            test("sem palavra-chave 27: busca por termo de decisao", () =>
            {
                var results = FridaMemoryStore.Search("furações padrão", 3);
                Check(results != null, "Busca na memoria retornou nulo");
            });

            test("sem palavra-chave 28: contexto de projeto", () =>
            {
                var results = FridaMemoryStore.Search("diretório base de projetos", 3);
                Check(results != null, "Busca de contexto de projeto falhou");
            });

            test("sem palavra-chave 29: listagem de memorias", () =>
            {
                var list = FridaMemoryStore.ListMemories();
                Check(list.Count >= 3, "Catalogo de memoria deve conter as sementes padrao");
            });

            test("sem palavra-chave 30: memoria de fato operacional", () =>
            {
                var entry = FridaMemoryStore.WriteMemory("fact", "Turno da tarde", "O responsável pelo turno da tarde é o Marcos", null, "equipe,turno");
                Check(entry != null && FridaMemoryStore.ReadMemory(entry.Id) != null, "Gravacao e leitura de fato falhou");
            });

            test("sem palavra-chave 31: busca de licao aprendida", () =>
            {
                var entry = FridaMemoryStore.WriteMemory("user_lesson", "Compilação sem travas", "Executar compilação com nome temporário evita erro CS0016", null, "licao,compilador");
                var hits = FridaMemoryStore.Search("CS0016 compilação", 3);
                Check(hits.Count > 0, "Licao aprendida nao encontrada na busca ponderada");
            });

            test("sem palavra-chave 32: esquecer memoria", () =>
            {
                var temp = FridaMemoryStore.WriteMemory("fact", "Dado temporario", "Conteudo que sera esquecido", null, "temp");
                Check(FridaMemoryStore.ForgetMemory(temp.Id), "Esquecer memoria falhou");
                Check(FridaMemoryStore.ReadMemory(temp.Id) == null, "Memoria ainda acessivel apos esquecer");
            });

            // Grupo 5: Conversa / Explicações Teóricas (8 casos)
            test("sem palavra-chave 33: saudacao matinal", () =>
            {
                var intent = FridaRequestIntent.Classify("bom dia frida, tudo bem por ai?");
                Check(!intent.RequiresTools, "Saudacao matinal acionou ferramentas indevidamente");
            });

            test("sem palavra-chave 34: agradecimento casual", () =>
            {
                var intent = FridaRequestIntent.Classify("obrigado pela ajuda, ficou otimo!");
                Check(!intent.RequiresTools, "Agradecimento acionou ferramentas indevidamente");
            });

            test("sem palavra-chave 35: conceito teorico de algoritmo", () =>
            {
                var intent = FridaRequestIntent.Classify("o que e um algoritmo de ordenacao rapida?");
                Check(intent.IsExplanation || !intent.RequiresTools, "Conceito teorico acionou acao indevida");
            });

            test("sem palavra-chave 36: explicacao de motor eletrico", () =>
            {
                var intent = FridaRequestIntent.Classify("me explique como funciona um motor eletrico de inducao");
                Check(intent.IsExplanation || !intent.RequiresTools, "Explicacao de motor acionou acao indevida");
            });

            test("sem palavra-chave 37: diferenca entre acos", () =>
            {
                var intent = FridaRequestIntent.Classify("qual a diferenca entre aco laminado a quente e a frio?");
                Check(intent.IsExplanation || !intent.RequiresTools, "Diferenca tecnica teorica acionou ferramentas fisicas");
            });

            test("sem palavra-chave 38: redacao criativa de saudacao", () =>
            {
                var intent = FridaRequestIntent.Classify("escreva uma saudacao formal para inicio de expediente");
                Check(!intent.RequiresTools, "Redacao criativa acionou ferramentas fisicas");
            });

            test("sem palavra-chave 39: despedida de fim de expediente", () =>
            {
                var intent = FridaRequestIntent.Classify("boa noite frida, ate amanha");
                Check(!intent.RequiresTools, "Despedida acionou ferramentas");
            });

            test("sem palavra-chave 40: conceito de fisica basica", () =>
            {
                var intent = FridaRequestIntent.Classify("o que significa torque em termos simples?");
                Check(intent.IsExplanation || !intent.RequiresTools, "Explicacao simples acionou ferramentas");
            });
        }
    }
}
