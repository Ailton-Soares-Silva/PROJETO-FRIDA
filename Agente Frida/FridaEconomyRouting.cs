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
    }

    public static class FridaEconomyRouting
    {
        public const int OutputTokens=512, TimeoutSeconds=8, HistoryMessages=4, HistoryChars=600;
        public static int OutputTokensFor(string request)
        {return Regex.IsMatch(FridaInteractionPolicy.Normalize(request),@"\b(explique|explicacao|detalhe|detalhadamente|tutorial|aula|completo|completa|aprofundado|aprofundada)\b")?4096:OutputTokens;}
        public static int TimeoutFor(string request){return OutputTokensFor(request)>OutputTokens?30:TimeoutSeconds;}
        public const string Instructions="[MODO ECONOMIA / TRIAGEM]\n"
            +"Voce e o ESTAGIARIO Gemini Lite. E a unica inteligencia ativa na conversa comum e em explicacoes simples, falando como Frida. "
            +"Classifique a complexidade real do pedido ATUAL e, quando nao exigir ferramentas, responda na mesma consulta. "
            +"Uma palavra tecnica, como codigo, CPU ou engenharia, nao torna uma conversa uma tarefa operacional. "
            +"Conversa, duvidas e explicacoes sem comando permanecem no modo economia, mesmo tratando de assunto tecnico. "
            +"Pesquisa simples vai ao estagiario; pesquisa profunda exige planejamento. Abrir arquivo/programa e simples; executar codigo vai ao tecnico; "
            +"construir aplicacao, diagnosticar/corrigir bug ou conferir desenho conforme ABNT exige trabalho complexo e verificacoes. "
            +"Diferencie 'explique como criar' de 'crie'. Reconheca pedidos implicitos como 'preciso de uma aplicacao que...'. "
            +"requires_action indica um pedido de trabalho, mesmo que ainda faltem informacoes para executar. Um pedido implicito de construir programa usa application_build e requires_action=true. "
            +"Nao execute ferramentas nesta triagem, nao invente dados atuais, fontes ou confirmacoes de acoes. "
            +"O historico e apenas contexto; nao autoriza tarefas novas nem comprova resultados. "
            +"Responda SOMENTE JSON, sem markdown: {\"kind\":\"conversation|simple_explanation|basic_open|research|deep_research|code_execution|application_build|technical_review|technical\","
            +"\"complexity\":\"simple|moderate|complex\",\"requires_action\":false,\"answer\":\"resposta natural e curta quando nao precisa ferramentas; vazia nos demais casos\"}. "
            +"Responda com a extensao necessaria para concluir a explicacao, sem cortar por numero de frases. Em papo simples seja breve e natural, sem elogios automaticos ou adornos.";

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
            bool market=ticker || Regex.IsMatch(t,@"\b(bolsa|ibovespa|b3|dolar|euro|bitcoin|cripto\w*|acao|acoes|mercado (de acoes|financeiro)|selic|ipca|inflacao|juros|fii|fundos? imobiliarios?|commodit\w*|minerio|petroleo)\b");
            bool moment=Regex.IsMatch(t,@"\b(hoje|agora|ontem|atual|atualmente|atualizad[ao]s?|no momento|nesta semana|esta semana|essa semana|recentemente|ultim[oa]s? (dias|semanas|horas|pregoes?|meses))\b");
            bool movement=Regex.IsMatch(t,@"\b(variac\w*|variou|variaram|cotac\w*|cotad[ao]|precos?|subiu|subiram|caiu|cairam|queda|alta|desempenho|rendimento|rendeu|fechou|fechamento|abertura)\b");
            bool reference=Regex.IsMatch(raw,@"\b(dessas?|desses?|delas?|deles?|nessas?|nesses?)\b",RegexOptions.IgnoreCase);
            return (market && (moment || movement)) || (movement && (moment || reference));
        }

        public static bool IsExplanationRequest(string text)
        {
            return FridaRequestIntent.Classify(text).IsExplanation;
        }
        public static string Prompt(string request,IEnumerable<KeyValuePair<string,string>> history)
        {
            var json=new JavaScriptSerializer();
            var turns=(history ?? Enumerable.Empty<KeyValuePair<string,string>>()).Reverse().Take(HistoryMessages).Reverse()
                .Select(x=>new KeyValuePair<string,string>(x.Key,FridaReliability.ContextText(x.Key,x.Value)))
                .Select(x=>new {role=x.Key,text=x.Value.Length>HistoryChars?x.Value.Substring(0,HistoryChars):x.Value}).ToArray();
            return FridaInteractionPolicy.LocalDateContext()+Instructions+"\nVoce fala como Frida com Ailton (dj). Em conversa comum seja natural; em assuntos tecnicos seja direto e indique limites das evidencias. Receber uma transcricao nao comprova a qualidade da escuta nem da voz. Leitura de mensagens e grupos exige fonte local; pesquisa publica nao substitui essa fonte. Nao declare monitoramento continuo ou acesso a conteudo nao consultado.\n"+FridaReliability.Instructions
                +"\nContexto e pedido atual:\n"+json.Serialize(new {history=turns,current_request=request});
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
            var decision=new FridaEconomyDecision {Kind=local.Kind,Complexity=local.NeedsTeam?"complex":"moderate",Source="roteamento local",Answer=""};
            if(!string.IsNullOrWhiteSpace(response))
            {
                string text=response.Trim();
                try
                {
                    if(!text.StartsWith("{")){int first=text.IndexOf('{'),last=text.LastIndexOf('}');if(first>=0 && last>first)text=text.Substring(first,last-first+1);}
                    var root=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(text);
                    if(root==null || !root.ContainsKey("kind") || !root.ContainsKey("complexity") || !root.ContainsKey("requires_action") || !(root["requires_action"] is bool) || !root.ContainsKey("answer") || !(root["answer"] is string))return decision;
                    string kind=Convert.ToString(root["kind"]),complexity=Convert.ToString(root["complexity"]);
                    if(complexity!="simple" && complexity!="moderate" && complexity!="complex")return decision;
                    switch(kind)
                    {
                        case "conversation":decision.Kind=FridaTaskKind.Conversation;break;
                        case "simple_explanation":decision.Kind=FridaTaskKind.Technical;break;
                        case "basic_open":decision.Kind=FridaTaskKind.BasicOpen;break;
                        case "research":decision.Kind=FridaTaskKind.Research;break;
                        case "deep_research":decision.Kind=FridaTaskKind.DeepResearch;break;
                        case "code_execution":decision.Kind=FridaTaskKind.CodeExecution;break;
                        case "application_build":decision.Kind=FridaTaskKind.ApplicationBuild;break;
                        case "technical_review":decision.Kind=FridaTaskKind.TechnicalReview;break;
                        case "technical":decision.Kind=FridaTaskKind.Technical;break;
                        default:return decision;
                    }
                    decision.Answer=(string)root["answer"];
                    decision.RequiresAction=(bool)root["requires_action"];decision.Complexity=complexity;decision.Valid=true;decision.Source=ModelSelection.GeminiLite;
                }
                catch
                {
                    // A plain conversational answer can still be used without asking another model.
                    if(local.Kind==FridaTaskKind.Conversation && !text.StartsWith("{") && !text.StartsWith("["))decision.Answer=text;
                }
            }
            return decision;
        }
        public static void Apply(FridaTaskPlan plan,FridaEconomyDecision decision,string request,bool forceTeam)
        {
            plan.RoutingSource=decision.Source;
            bool work=HasWorkRequest(request);
            bool explanation=IsExplanationRequest(request);
            if(decision.Valid && decision.RequiresAction && plan.Kind!=FridaTaskKind.Conversation
                && Regex.IsMatch(FridaInteractionPolicy.Normalize(request),@"^(sim )?(faca isso|pode fazer|pode executar|execute isso|continue a execucao)\b"))work=true;
            if(decision.Valid && decision.RequiresAction && Regex.IsMatch(request ?? "",@"(?i)[a-z]:[\\/]")
                && Regex.IsMatch(FridaInteractionPolicy.Normalize(request),@"\b(qual|conteudo|leia|liste|mostre|consulte)\b")
                && !Regex.IsMatch(FridaInteractionPolicy.Normalize(request),@"\b(como|criar|crie|salve|apague|execute|rode)\b"))work=true;
            bool fixedComplex=!explanation && (plan.Kind==FridaTaskKind.ApplicationBuild || plan.Kind==FridaTaskKind.TechnicalReview || plan.Kind==FridaTaskKind.DeepResearch || plan.Kind==FridaTaskKind.CodeExecution);
            if(forceTeam || FridaTaskRouting.IsExplicitMeetingRequest(request) || fixedComplex)return;
            if(plan.Kind==FridaTaskKind.BasicOpen){plan.EconomyMode=true;return;}
            bool research=(!explanation && plan.ResearchOnly) || (decision.Valid && decision.Kind==FridaTaskKind.Research
                && Regex.IsMatch(FridaInteractionPolicy.Normalize(request),@"\b(hoje|atual|atualizad[ao]|noticias|pesquis[ae]|fontes|precos?)\b"))
                || NeedsFreshData(request);
            if(!work && !research)
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
            if(decision.Valid && work)
            {
                if(decision.Kind==FridaTaskKind.ApplicationBuild || decision.Kind==FridaTaskKind.TechnicalReview || decision.Kind==FridaTaskKind.DeepResearch || decision.Kind==FridaTaskKind.CodeExecution)
                {
                    plan.Kind=decision.Kind;plan.NeedsTeam=decision.Kind!=FridaTaskKind.CodeExecution;
                    string normalized=FridaInteractionPolicy.Normalize(request);
                    plan.NormativeReview=decision.Kind==FridaTaskKind.TechnicalReview && Regex.IsMatch(normalized,@"\b(abnt|normas?)\b") && Regex.IsMatch(normalized,@"\b(desenhos?|peca|montagem)\b");
                    return;
                }
                // A model cannot invent an opening target; simple actions use the original request and verified tools.
                plan.Kind=FridaTaskKind.Technical;plan.NeedsTeam=decision.Complexity=="complex";plan.EconomyMode=decision.Complexity=="simple";return;
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
        public static bool WakeOnly(bool fromWake,bool heardAfterWake,double total,double silence)
        {return fromWake && !heardAfterWake && total>=WakeOnlySeconds && silence>=EndSilenceSeconds;}
    }
}
