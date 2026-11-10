using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public sealed class FridaEconomyDecision
    {
        public FridaTaskKind Kind;
        public string Complexity, Answer, Source;
        public bool RequiresAction, Valid, AnswerDirectly;
        public string Objective;
        public bool NeedsTools;
        public bool NeedsWeb;
        public bool NeedsLocalData;
        public List<FridaTaskStep> Steps = new List<FridaTaskStep>();
        public List<string> CandidateSkills = new List<string>();
        public string ClarificationQuestion;
        public FridaStructuredPlan StructuredPlan;
    }

    public static class FridaEconomyRouting
    {
        public const int OutputTokens=512, TimeoutSeconds=8, HistoryMessages=6, HistoryChars=2400;
        public static int OutputTokensFor(string request)
        {
            string norm = FridaInteractionPolicy.Normalize(request);
            if (Regex.IsMatch(norm, @"\b(explique|explicacao|detalhe|detalhadamente|tutorial|aula|completo|completa|aprofundado|aprofundada)\b"))
                return 4096;
            if ((request ?? "").Length > 300)
                return 1024;
            return OutputTokens;
        }
        public static int TimeoutFor(string request)
        {
            int tokens = OutputTokensFor(request);
            if (tokens >= 4096) return 30;
            if (tokens >= 1024) return 18;
            return TimeoutSeconds;
        }
        public const string Instructions="[MODO ECONOMIA / TRIAGEM]\n"
            +"Voce e a triagem rapida da FRIDA operando na bancada privada de Ailton Soares (dj) com ACESSO TOTAL ao sistema Windows 11 e a web. "
            +"Ferramentas reais disponiveis no motor tecnico da Frida: open_program_or_url (abre programas, arquivos e URLs https://... diretamente no navegador e na tela de Ailton), inspect_screen (captura e enxerga a tela atual de Ailton), run_powershell (automacao completa de Windows, janelas e processos), manage_files (leitura/gravacao de arquivos), web_research (pesquisa web atualizada de produtos, marcas, lojas, estoques, catalogos e cotacoes) e read_whatsapp. "
            +"NUNCA responda dizendo 'nao posso interagir com sua tela', 'nao consigo controlar o navegador' ou 'nao tenho acesso ao estoque': qualquer pedido de tela, navegador, abertura, sistema ou consulta de estoque/loja/marca/produto na web EXIGE requires_action=true e answer=\"\"! "
            +"Classifique a complexidade real do pedido ATUAL e, somente quando for conversa pura ou explicacao conceitual que NAO dependa de ferramentas nem de busca na web, responda na mesma consulta. "
            +"Uma palavra tecnica ou comentario casual (ex: comentar sobre um jogo 'pra poder rodar a 30 fps') sem pedido a Frida permanece em conversation com requires_action=false. "
            +"Pesquisa simples, estoque de loja, marca ou produto vai a research com requires_action=true; pesquisa profunda exige deep_research. Abrir arquivo/programa simples usa basic_open; abrir navegador com pesquisa, colocar pagina/produto na tela, inspecionar a tela ou executar comandos usa technical com requires_action=true; "
            +"construir aplicacao, diagnosticar/corrigir bug ou conferir desenho conforme ABNT exige trabalho complexo e verificacoes. "
            +"Diferencie 'explique como criar' de 'crie'. Reconheca pedidos implicitos como 'preciso de uma aplicacao que...'. "
            +"Use skill_evolution com requires_action=true somente quando o operador pede que a Frida aprenda, guarde ou atualize um procedimento reutilizavel (uma skill), com qualquer redacao; quando e o operador quem quer aprender ou entender algo, e conversation ou simple_explanation. "
            +"requires_action indica um pedido de trabalho ou pesquisa, mesmo que ainda faltem informacoes para executar. "
            +"Nao execute ferramentas nesta triagem, nao invente dados atuais, estoques, fontes ou confirmacoes de acoes. "
            +"O historico e apenas contexto; nao autoriza tarefas novas nem comprova resultados. "
            +"Responda SOMENTE JSON, sem markdown: {\"kind\":\"conversation|simple_explanation|basic_open|research|deep_research|code_execution|application_build|technical_review|technical|skill_evolution\","
            +"\"complexity\":\"simple|moderate|complex\",\"requires_action\":false,\"answer\":\"resposta natural e curta somente quando nao precisa ferramentas nem web; vazia nos demais casos\"}. "
            +"Responda com a extensao necessaria para concluir a explicacao, sem cortar por numero de frases. Em papo simples seja breve e natural, sem elogios automaticos, sem sermoes e sem recusas.";

        public static bool HasWorkRequest(string text)
        {
            return FridaRequestIntent.Classify(text).RequiresTools;
        }
        /// <summary>
        /// Pedido que depende de dados do momento (cotacao, variacao, mercado "hoje"). Nunca pode ser respondido
        /// direto pela triagem: em 03/11 ela inventou "BBAS3 subiu nos ultimos dias" quando a acao caiu ~30%.
        /// </summary>
        public static bool NeedsFreshData(string text)
        {
            string raw=text ?? "";
            string t=FridaInteractionPolicy.Normalize(raw);
            bool ticker=Regex.IsMatch(raw,@"\b[A-Z]{4}\d{1,2}\b");
            bool market=ticker || Regex.IsMatch(t,@"\b(bolsa|ibovespa|b3|dolar|euro|bitcoin|cripto\w*|acao|acoes|mercado (de acoes|financeiro)|selic|ipca|inflacao|juros|fii|fundos? imobiliarios?|commodit\w*|minerio|petroleo|arroba|boi gordo|milho|soja|cafe|ouro|cambio|moeda americana)\b");
            bool moment=Regex.IsMatch(t,@"\b(hoje|agora|ontem|atual|atualmente|atualizad[ao]s?|no momento|nesta semana|esta semana|essa semana|recentemente|fim de semana|ultim[oa]s? (dias|semanas|horas|pregoes?|meses))\b");
            bool movement=Regex.IsMatch(t,@"\b(variac\w*|variou|variaram|cotac\w*|cotad[ao]|precos?|subiu|subiram|caiu|cairam|queda|alta|desempenho|rendimento|rendeu|fechou|fechamento|abertura|valor|quanto custa|custa|preco medio)\b");
            bool reference=Regex.IsMatch(raw,@"\b(dessas?|desses?|delas?|deles?|nessas?|nesses?|ess[ae]s?|est[ae]s?)\b",RegexOptions.IgnoreCase);
            bool weatherOrTraffic = Regex.IsMatch(t, @"\b(previsao (?:do tempo|de chuva|de tempo)?|tempo em|chuva para|transito na|transito no|rodovia|clima em)\b");
            bool externalFacts = Regex.IsMatch(t, @"\b(norma\b|dimensoes\b|dimensões\b|bitola\b|preco medio\b|quanto custa\b|tabela de conversao\b|tabela dimensional\b)");
            bool nonWebLocal=Regex.IsMatch(t,@"\b(whatsapp|whats|wpp|zap|arquivo|arquivos|pasta|pastas|diretorio|diretorios|disco|cpu|memoria|processo|processos)\b");
            bool storeOrBrand=!nonWebLocal && (
                Regex.IsMatch(t,@"\b(estoque|em estoque|unidades disponiveis|tem unidades|pronta entrega|esgotad[oa]|onde comprar)\b")
                || (Regex.IsMatch(t,@"\b(disponivel|disponiveis|disponibilidade)\b") && Regex.IsMatch(t,@"\b(estoque|loja|site|unidades|compra|comprar|venda|procreto|marca|filamento|produto|peca|modelo|catalogo)\b"))
                || Regex.IsMatch(t,@"^e (?:a |o )?(?:marca|loja|fabricante|fornecedor)\b"));
            return (market && (moment || movement)) || (movement && (moment || reference)) || storeOrBrand || weatherOrTraffic || externalFacts;
        }

        public static bool IsExplanationRequest(string text)
        {
            return FridaRequestIntent.Classify(text).IsExplanation;
        }
        public static string Prompt(string request,IEnumerable<KeyValuePair<string,string>> history)
        {
            string skillsSummary = null;
            try
            {
                var skills = FridaSkillRegistry.ListSkills();
                if (skills != null && skills.Count > 0)
                {
                    skillsSummary = string.Join("\n", skills.Select(s => "- " + s.Id + " (" + s.Name + "): " + s.Description).Take(20).ToArray());
                }
            }
            catch { }
            return FridaPlanner.BuildPrompt(request, history, null, skillsSummary);
        }
        /// <summary>Resposta de triagem aproveitavel (JSON com os campos exigidos). Provedores gratuitos que falham aqui cedem a vez ao proximo.</summary>
        public static bool LooksLikeDecision(string response)
        {
            if(string.IsNullOrWhiteSpace(response))return false;
            var probe=Parse(response,new FridaTaskPlan {Kind=FridaTaskKind.Technical});
            return probe.Valid;
        }
        public static FridaEconomyDecision Parse(string response,FridaTaskPlan local)
        {
            var structured = FridaPlanner.Parse(response, local);
            var decision = new FridaEconomyDecision
            {
                Kind = structured.Kind,
                Complexity = structured.Complexity,
                Source = structured.Source,
                Answer = structured.AnswerDirectly ?? "",
                RequiresAction = structured.NeedsTools,
                NeedsTools = structured.NeedsTools,
                NeedsWeb = structured.NeedsWeb,
                NeedsLocalData = structured.NeedsLocalData,
                Objective = structured.Objective,
                Steps = structured.Steps ?? new List<FridaTaskStep>(),
                CandidateSkills = structured.CandidateSkills ?? new List<string>(),
                ClarificationQuestion = structured.ClarificationQuestion ?? "",
                StructuredPlan = structured,
                Valid = structured.Valid
            };
            return decision;
        }
        public static void Apply(FridaTaskPlan plan,FridaEconomyDecision decision,string request,bool forceTeam)
        {
            plan.RoutingSource=decision.Source;
            if(!string.IsNullOrWhiteSpace(decision.Objective)) plan.Objective = decision.Objective;
            plan.NeedsWeb = decision.NeedsWeb;
            plan.NeedsLocalData = decision.NeedsLocalData;
            if(decision.Steps != null && decision.Steps.Count > 0)
            {
                plan.Steps.Clear();
                plan.Steps.AddRange(decision.Steps);
            }
            if(decision.CandidateSkills != null && decision.CandidateSkills.Count > 0)
            {
                foreach(string sk in decision.CandidateSkills)
                    if(!plan.CandidateSkills.Contains(sk)) plan.CandidateSkills.Add(sk);
            }
            if(!string.IsNullOrWhiteSpace(decision.ClarificationQuestion))
                plan.ClarificationQuestion = decision.ClarificationQuestion;
            var classified=FridaRequestIntent.Classify(request);
            bool work=classified.RequiresTools;
            bool pureResearchIntent=classified.Operations.Count>0 && classified.Operations.All(o=>o.Kind=="research");
            bool explanation=classified.IsExplanation;
            bool triageRefusal=!string.IsNullOrWhiteSpace(decision.Answer) && FridaInteractionPolicy.IsCannedModelRefusal(decision.Answer);
            if(triageRefusal)
            {
                decision.Answer="";
                decision.AnswerDirectly=false;
            }
            if(decision.Valid && decision.RequiresAction && plan.Kind!=FridaTaskKind.Conversation
                && Regex.IsMatch(FridaInteractionPolicy.Normalize(request),@"^(sim )?(faca isso|pode fazer|pode executar|execute isso|continue a execucao)\b"))work=true;
            if(decision.Valid && decision.RequiresAction && Regex.IsMatch(request ?? "",@"(?i)[a-z]:[\\/]")
                && Regex.IsMatch(FridaInteractionPolicy.Normalize(request),@"\b(qual|conteudo|leia|liste|mostre|consulte)\b")
                && !Regex.IsMatch(FridaInteractionPolicy.Normalize(request),@"\b(como|criar|crie|salve|apague|execute|rode)\b"))work=true;
            bool fixedComplex=!explanation && (plan.Kind==FridaTaskKind.ApplicationBuild || plan.Kind==FridaTaskKind.TechnicalReview || plan.Kind==FridaTaskKind.DeepResearch || plan.Kind==FridaTaskKind.CodeExecution || plan.Kind==FridaTaskKind.SkillEvolution);
            if(plan.Kind==FridaTaskKind.SkillEvolution){plan.NeedsTeam=true;plan.EconomyMode=false;plan.AnswersWithoutTools=false;decision.AnswerDirectly=false;decision.Answer="";return;}
            if(forceTeam || FridaTaskRouting.IsExplicitMeetingRequest(request) || fixedComplex)return;
            if(plan.Kind==FridaTaskKind.BasicOpen){plan.EconomyMode=true;return;}
            // Uma pesquisa com acao na tela nao pode perder a etapa de navegador na triagem.
            if(plan.RequiresInteractiveTools)
            {
                plan.Kind=FridaTaskKind.Technical;plan.EconomyMode=false;plan.AnswersWithoutTools=false;
                decision.AnswerDirectly=false;decision.Answer="";return;
            }
            bool research=(!explanation && plan.ResearchOnly) || pureResearchIntent || (decision.Valid && (decision.Kind==FridaTaskKind.Research || decision.Kind==FridaTaskKind.DeepResearch)
                && (decision.RequiresAction || Regex.IsMatch(FridaInteractionPolicy.Normalize(request),@"\b(hoje|atual|atualizad[ao]|noticias|pesquis[ae]|fontes|precos?|estoque|disponiv\w*|marca|loja)\b")))
                || NeedsFreshData(request);
            if(triageRefusal && !work && !research)
            {
                plan.Kind=FridaTaskKind.Technical;plan.EconomyMode=false;plan.AnswersWithoutTools=false;return;
            }
            bool mentionsLocalEntities = Regex.IsMatch(request ?? "", @"(?i)\b(skill|skills|habilidade|planilha|planilhas|drive|whatsapp|whats|chassi|chassis|placa|placas|kanban|copac|pop|pcp|ordem|protheus)\b") || Regex.IsMatch(request ?? "", @"(?i)[a-z]:[\\/]");
            if (mentionsLocalEntities)
            {
                decision.AnswerDirectly = false;
                decision.Answer = "";
                plan.AnswersWithoutTools = false;
                plan.EconomyMode = false;
                if (plan.Kind == FridaTaskKind.Conversation) plan.Kind = FridaTaskKind.Technical;
            }
            if(!mentionsLocalEntities && !work && !research && !(decision.Valid && decision.RequiresAction))
            {
                if(!string.IsNullOrWhiteSpace(decision.Answer))
                {
                    decision.AnswerDirectly=true;
                    plan.Kind=decision.Valid && decision.Kind==FridaTaskKind.Conversation && !decision.RequiresAction?FridaTaskKind.Conversation
                        :plan.Kind==FridaTaskKind.Conversation?FridaTaskKind.Conversation:FridaTaskKind.Technical;
                    plan.NeedsTeam=false;plan.EconomyMode=true;plan.AnswersWithoutTools=true;return;
                }
                decision.AnswerDirectly=false;
            }
            if(pureResearchIntent && (!decision.Valid || decision.Kind==FridaTaskKind.Research || decision.Kind==FridaTaskKind.DeepResearch || plan.ResearchOnly))
            {
                plan.Kind=decision.Valid && decision.Kind==FridaTaskKind.DeepResearch?FridaTaskKind.DeepResearch:FridaTaskKind.Research;
                plan.NeedsTeam=plan.Kind==FridaTaskKind.DeepResearch;plan.EconomyMode=!plan.NeedsTeam;return;
            }
            if(decision.Valid && (work || decision.RequiresAction))
            {
                if(decision.Kind==FridaTaskKind.ApplicationBuild || decision.Kind==FridaTaskKind.TechnicalReview || decision.Kind==FridaTaskKind.DeepResearch || decision.Kind==FridaTaskKind.CodeExecution || decision.Kind==FridaTaskKind.SkillEvolution)
                {
                    plan.Kind=decision.Kind;plan.NeedsTeam=decision.Kind!=FridaTaskKind.CodeExecution;
                    string normalized=FridaInteractionPolicy.Normalize(request);
                    plan.NormativeReview=decision.Kind==FridaTaskKind.TechnicalReview && Regex.IsMatch(normalized,@"\b(abnt|normas?)\b") && Regex.IsMatch(normalized,@"\b(desenhos?|peca|montagem)\b");
                    return;
                }
                if(decision.Kind==FridaTaskKind.Research)
                {
                    plan.Kind=FridaTaskKind.Research;plan.NeedsTeam=false;plan.EconomyMode=true;return;
                }
                // A model cannot invent an opening target; operational/technical actions use the original request and verified tools with full round/token budget.
                plan.Kind=FridaTaskKind.Technical;plan.NeedsTeam=decision.Complexity=="complex";plan.EconomyMode=false;return;
            }
            if(research)
            {
                plan.Kind=decision.Valid && decision.Kind==FridaTaskKind.DeepResearch?FridaTaskKind.DeepResearch:FridaTaskKind.Research;
                plan.NeedsTeam=plan.Kind==FridaTaskKind.DeepResearch;plan.EconomyMode=!plan.NeedsTeam;
            }
            // If the classifier failed on a real command, preserve the local technical route.
            if(work && plan.Kind==FridaTaskKind.Conversation)plan.Kind=FridaTaskKind.Technical;
        }
    }

    public static class FridaVoiceTiming
    {
        public const double EndSilenceSeconds=0.60, MinimumCaptureSeconds=0.75, WakeOnlySeconds=0.65;
        public static bool EndBySilence(bool heard,double total,double silence)
        {return heard && total>=MinimumCaptureSeconds && silence>=EndSilenceSeconds;}
        public static bool EndBySilence(bool heard,double total,double silence,double threshold)
        {return heard && total>=MinimumCaptureSeconds && silence>=threshold;}
        public static bool WakeOnly(bool fromWake,bool heardAfterWake,double total,double silence)
        {return fromWake && !heardAfterWake && total>=WakeOnlySeconds && silence>=EndSilenceSeconds;}
    }
}
