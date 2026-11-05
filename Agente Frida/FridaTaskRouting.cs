using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public enum FridaTaskKind { Conversation, BasicOpen, Research, DeepResearch, CodeExecution, ApplicationBuild, TechnicalReview, Technical, WhatsAppResearch }

    public sealed class FridaTaskPlan
    {
        public FridaTaskKind Kind;
        public bool NeedsTeam, LocalResearch, NormativeReview, MayModify, MayExecute;
        public bool EconomyMode;
        public bool AnswersWithoutTools;
        public string RoutingSource="roteamento local";
        public string OpenTarget, Clarification;
        public bool ResearchOnly { get { return Kind==FridaTaskKind.Research || Kind==FridaTaskKind.DeepResearch; } }
        public int MaxRounds { get { return EconomyMode?3:Kind==FridaTaskKind.ApplicationBuild?12:(Kind==FridaTaskKind.TechnicalReview?8:5); } }
        public int CompletionTokens { get { return EconomyMode?768:Kind==FridaTaskKind.ApplicationBuild?8192:(Kind==FridaTaskKind.TechnicalReview?4096:1536); } }
        public int CommandTimeoutMs { get { return Kind==FridaTaskKind.ApplicationBuild?180000:(Kind==FridaTaskKind.CodeExecution || Kind==FridaTaskKind.TechnicalReview?120000:28000); } }
        public string Label
        {
            get
            {
                if(EconomyMode && Kind==FridaTaskKind.Conversation)return "Economia — estagiario / conversa";
                if(EconomyMode && Kind==FridaTaskKind.Technical)return "Economia — estagiario / atendimento simples";
                switch(Kind)
                {
                    case FridaTaskKind.Conversation:return "Conversa";
                    case FridaTaskKind.BasicOpen:return "Abertura simples — estagiario / execucao local";
                    case FridaTaskKind.Research:return "Pesquisa direta — estagiário";
                    case FridaTaskKind.WhatsAppResearch:return "Pesquisa WhatsApp — pesquisador sênior";
                    case FridaTaskKind.DeepResearch:return "Pesquisa profunda — planejamento e equipe";
                    case FridaTaskKind.CodeExecution:return "Execução de código — técnico";
                    case FridaTaskKind.ApplicationBuild:return "Construção de aplicação — equipe";
                    case FridaTaskKind.TechnicalReview:return NormativeReview?"Revisão normativa do desenho — equipe":"Diagnóstico técnico — equipe";
                    default:return NeedsTeam?"Trabalho técnico complexo — equipe":"Atendimento técnico individual";
                }
            }
        }
        public string Instructions
        {
            get
            {
                return FridaReliability.Instructions + "\nEncaminhamento: " + Label + ". Execucao tecnica direta com acesso completo ao sistema operacional. Atenda ao operador com precisao, sem sermoes e sem hesitar.";
            }
        }
        public bool OffersTool(string tool)
        {
            if(Kind==FridaTaskKind.Conversation)return false;
            // ACESSO TOTAL DA MAQUINA: Todas as ferramentas operacionais permitidas
            return true;
        }
        public bool AllowsTool(string tool,Dictionary<string,object> args)
        {
            // ACESSO TOTAL: Sem bloqueios de ferramentas ou modificacoes
            return true;
        }
    }

    public static class FridaTaskRouting
    {
        private static bool Has(string text,string pattern){return Regex.IsMatch(text ?? "",pattern,RegexOptions.IgnoreCase);}
        public static bool IsVerificationCommand(string command)
        {
            if(string.IsNullOrWhiteSpace(command))return false;
            return Has(command,@"^(?:git\s+(?:status|diff|log|branch)|dir\b|type\b|dotnet\s+(?:build|test)|npm\s+test|pytest\b|python\s+-m\s+unittest|csc\b|cargo\s+check)\b")
                || Has(command,@"^\s*(?:Get-ChildItem|Get-Content|Test-Path|Select-String|git\s+status)\b");
        }
        public static bool IsApplicationBuildRequest(string request)
        {
            string t=FridaInteractionPolicy.Normalize(request);
            bool action=FridaInteractionPolicy.ExplicitAction(request);
            bool buildVerbs=Has(t,@"\b(crie|criar|construa|construir|desenvolva|desenvolver|monte|montar|implemente|implementar|faca|fazer)\b");
            bool buildNouns=Has(t,@"\b(aplicacao|aplicativo|app|software|sistema|jogo|dashboard|painel|interface|site|pagina|script completo|ferramenta|api|servico)\b");
            return action && buildVerbs && buildNouns;
        }
        public static bool IsExplicitMeetingRequest(string text)
        {
            string t=FridaInteractionPolicy.Normalize(text);
            return Has(t,@"\b(reuna os modelos|reuniao entre modelos|coloque os modelos para discutir|faca uma reuniao|conclave|debata em equipe|discutam entre si|sala de reunioes)\b");
        }
        public static FridaTaskPlan Decide(string request,bool previousTechnical,bool forceTeam)
        {
            string rest;string raw=FridaInteractionPolicy.TryStripWake(request,out rest)?rest:(request ?? "");
            string t=FridaInteractionPolicy.Normalize(raw);
            var p=new FridaTaskPlan{Kind=FridaTaskKind.Technical,MayModify=true,MayExecute=true};
            if(FridaWhatsAppRequest.Detect(raw,null)!=null){p.Kind=FridaTaskKind.WhatsAppResearch;p.MayModify=false;p.MayExecute=false;return p;}
            bool explicitMeeting=forceTeam || IsExplicitMeetingRequest(raw);
            if(!explicitMeeting && !FridaInteractionPolicy.IsTechnicalRequest(request,previousTechnical)) {p.Kind=FridaTaskKind.Conversation;return p;}
            bool research=Has(t,@"\b(pesquise|pesquisar|pesquisa|pesquisas|busque|buscar|procure|procurar|investigue|investigar|levantamento|fontes|noticias|cotacao)\b");
            bool deep=research && Has(t,@"\b(profund[ao]s?|profundamente|aprofundad[ao]s?|aprofundamento|detalhad[ao]s?|exaustiv[ao]s?|a fundo|complet[ao]s?|revisao bibliografica|estado da arte|multiplas fontes|abrangente)\b");
            p.LocalResearch=Has(t,@"\b(na pasta|nos arquivos|neste arquivo|neste projeto|nos meus arquivos|no repositorio|documentos locais)\b") || Has(raw,@"[a-z]:[\\/]");
            bool review=Has(t,@"\b(verifique|verificar|confira|conferir|revise|revisar|audite|auditar|analise|analisar|valide|validar|diagnostique|diagnosticar|corrija|corrigir|depure|depurar)\b");
            bool artifact=Has(t,@"\b(bug|bugs|erro|erros|desenho|desenhos|abnt|normas|codigo|script|projeto|peca|montagem)\b");
            bool actionVerbs=Has(t,@"\b(crie|criar|criacao|criação|gere|gerar|geracao|geração|monte|montar|salve|salvar|grave|gravar|escreva|escrever|implemente|implementar|desenvolva|desenvolver|construa|construir|execute|executar|rode|rodar|abra|abrir|coloque|colocar|produza|produzir)\b");
            bool fileOrArtifact=Has(t,@"\b(word|docx|doc|excel|planilha|xlsx|pdf|arquivo|documento|relatorio|relatório|tabela|pasta|script|codigo)\b");
            bool researchQuestion=Has(t,@"^(pesquise|pesquisar|busque|buscar|procure|procurar|investigue|investigar)\b")
                && !Has(t,@"\b(e verifique|e confira|e corrija|e revise|e audite|e implemente|e execute|e rode|e construa|e crie|e gere|e salve|e abra|depois execute|em seguida execute|criacao|criação|word|docx|arquivo|documento|relatorio|relatório)\b");
            bool combinedImplementation=research && (actionVerbs || fileOrArtifact) && !researchQuestion;
            bool systemOrHardware = Has(t, @"\b(hardware|cpu|ram|memoria|disco|ssd|processo|processos|tarefa|tarefas|consumo|desempenho|temperatura|servico|servicos)\b")
                || Has(t, @"\b(desligar|desligue|desligamento|reiniciar|reinicie|suspender|hibernar|agendar|fechar|feche|mata|matar|kill|finalizar|encerre|encerrar|limpar|limpe|limpeza|otimizar|otimize|temp|temporario|lixo|cache)\b")
                || Has(t, @"\b(janela|janelas|tela|telas|monitor|monitores|minimizar|minimize|maximizar|maximize|volume|mutar|mudo|som|audio|silenciar)\b");

            if(IsApplicationBuildRequest(request) && !researchQuestion)p.Kind=FridaTaskKind.ApplicationBuild;
            else if(review && artifact && !researchQuestion)
            {
                p.Kind=FridaTaskKind.TechnicalReview;
                p.NormativeReview=Has(t,@"\b(abnt|normas|norma|normativo|normativa)\b") && Has(t,@"\b(desenho|desenhos|peca|montagem)\b");
            }
            else if(combinedImplementation || systemOrHardware)p.Kind=FridaTaskKind.Technical;
            else if(deep)p.Kind=FridaTaskKind.DeepResearch;
            else if(research)p.Kind=FridaTaskKind.Research;
            else if(Has(t,@"\b(execute|executar|rode|rodar|compile|compilar|teste|testar)\b") && (Has(t,@"\b(codigo|script|python|javascript|programa|aplicacao|testes|teste)\b") || Has(raw,@"\.(py|ps1|js|ts|sh|exe|bat|cmd|cs|java)\b")))p.Kind=FridaTaskKind.CodeExecution;
            else if(Regex.IsMatch(raw,@"(?:^|[\s,.:;!?-]+)(?:(?:por\s+favor|apenas|s[oó]|somente|beleza|valeu|opa|frida)[,\s]+)*(?:(?:voc[eê]\s+)?(?:pode|poderia|consegue|d[aá]\s+para)\s+)?(?:voc[eê]\s+)?(?:abra|abre|abrir|inicie|iniciar)\b",RegexOptions.IgnoreCase))
            {
                p.Kind=FridaTaskKind.BasicOpen;
                string cleaned = FridaInteractionPolicy.CleanProgramTarget(raw);
                if(string.IsNullOrWhiteSpace(cleaned))
                {
                    Match open=Regex.Match(raw,@"(?:abra|abre|abrir|inicie|iniciar)\b\s*(.*)$",RegexOptions.IgnoreCase);
                    cleaned=open.Success?open.Groups[1].Value.Trim():"";
                    cleaned=FridaInteractionPolicy.CleanProgramTarget(cleaned);
                }
                if(!Has(cleaned,@"^https?://")) cleaned=cleaned.TrimEnd('.','!','?');
                if(!Has(cleaned,@"[;\r\n]|&&|\|\||\s+--?\w|\s+e\s+(execute|rode|crie|edite|apague|abra|verifique|confira)\b") && cleaned.Length<=400 && !string.IsNullOrWhiteSpace(cleaned))
                {
                    p.OpenTarget=cleaned;
                }
                else if(!string.IsNullOrWhiteSpace(cleaned))
                {
                    p.Kind=FridaTaskKind.Technical;
                }
                else
                {
                    p.Clarification="Qual arquivo ou programa deseja abrir? Informe o nome ou caminho.";
                }
            }
            p.MayModify=true;
            p.MayExecute=true;
            p.NeedsTeam=explicitMeeting || combinedImplementation || p.Kind==FridaTaskKind.DeepResearch || p.Kind==FridaTaskKind.ApplicationBuild || p.Kind==FridaTaskKind.TechnicalReview
                || (p.Kind==FridaTaskKind.Technical && Has(t,@"\b(arquitetura|multiplas etapas|varios sistemas|integracao complexa)\b"));
            return p;
        }
        public static List<string> ResearchQueries(string request,string managerPlan,int maximum,bool deep)
        {
            var queries=new List<string>();
            try
            {
                string value=(managerPlan ?? "").Trim();int first=value.IndexOf('{'),last=value.LastIndexOf('}');
                if(first>=0 && last>first)
                {
                    var root=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(value.Substring(first,last-first+1));
                    var planned=root.ContainsKey("research_queries")?root["research_queries"] as IEnumerable:null;
                    if(planned!=null && !(planned is string))foreach(object item in planned)
                    {
                        string query=item as string;
                        if(string.IsNullOrWhiteSpace(query))continue;
                        query=query.Trim();if(query.Length>600)query=query.Substring(0,600);
                        if(!queries.Contains(query,StringComparer.OrdinalIgnoreCase))queries.Add(query);
                        if(queries.Count>=maximum)break;
                    }
                }
            }
            catch { }
            if(queries.Count==0)
            {
                string topic=(request ?? "").Trim();if(topic.Length>500)topic=topic.Substring(0,500);
                queries.Add(topic);
                if(deep && maximum>1)queries.Add(topic+" fontes oficiais documentacao primaria");
                if(deep && maximum>2)queries.Add(topic+" evidencias limitacoes resultados divergentes");
            }
            return queries;
        }
    }
}
