// Equipe hierarquica e telemetria de atividades reais, sem novos modelos.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public enum TeamRole { Secretary, Manager, Technician, Analyst, Assistant, Hearing, Voice, WhatsAppResearcher }

    public sealed class TeamMember
    {
        public TeamRole Role;
        public string Name, Bucket, Model, Sprite;
        public bool HasKey(FridaSettings s)
        {
            if (Bucket == "tts_edge") return true; // Edge-TTS dispensa chave
            string key = Bucket.Contains("cerebras") ? s.cerebras_api_key :
                Bucket.Contains("groq") ? s.groq_api_key : s.api_key;
            return !string.IsNullOrWhiteSpace(key);
        }
    }

    public sealed class TeamMessage
    {
        public TeamRole Role;
        public string Title, Model, Text, Recipients;
        public DateTime AtUtc;
        public bool Success;
    }

    public static class FridaTeamState
    {
        public const int IdleSeconds = 120;
        private static readonly object Sync = new object();
        public static readonly TeamMember[] Members = new TeamMember[]
        {
            new TeamMember {Role=TeamRole.Secretary, Name="Secretaria Frida", Bucket="gemini", Model=ModelSelection.GeminiLite, Sprite="priestess"},
            new TeamMember {Role=TeamRole.Manager, Name="Gestor / Cerebro", Bucket="gemini", Model=ModelSelection.GeminiComplex, Sprite="scholar"},
            new TeamMember {Role=TeamRole.Technician, Name="Tecnico / Engenheiro", Bucket="oai_groq", Model=ModelSelection.GroqBrain, Sprite="scout"},
            new TeamMember {Role=TeamRole.Analyst, Name="Analista / Auditor", Bucket="oai_cerebras", Model=ModelSelection.CerebrasBrain, Sprite="titan"},
            new TeamMember {Role=TeamRole.Assistant, Name="Estagiário / Pesquisa", Bucket="gemini", Model=ModelSelection.GeminiLite, Sprite="scribe"},
            new TeamMember {Role=TeamRole.WhatsAppResearcher, Name="Pesquisador sênior / WhatsApp", Bucket="gemini", Model=ModelSelection.GeminiComplex, Sprite="scholar"},
            new TeamMember {Role=TeamRole.Hearing, Name="Escuta / Whisper", Bucket="stt_groq", Model=ModelSelection.Transcription, Sprite="herald"},
            new TeamMember {Role=TeamRole.Voice, Name="Voz unica / Aoede", Bucket="tts", Model=ModelSelection.Speech, Sprite="priestess"}
        };
        private sealed class Activity
        {
            public bool Busy;
            public bool Meeting;
            public DateTime LastUtc = DateTime.UtcNow;
            public string Stage = "Disponivel no hall";
        }
        private static readonly Dictionary<TeamRole, Activity> Activities = Members.ToDictionary(x=>x.Role,x=>new Activity());
        private static readonly List<TeamMessage> Messages = new List<TeamMessage>();
        private static readonly HashSet<TeamRole> Participants = new HashSet<TeamRole>();
        private static int _meetingCount;
        private static string _report = "", _topic = "";
        public static bool IsMeetingRunning { get { lock(Sync) return _meetingCount > 0; } }
        public static string LastReport { get { lock(Sync) return _report; } }
        public static string Topic { get { lock(Sync) return _topic; } }
        public static string BriefStatus()
        {
            lock(Sync)
            {
                var text=new StringBuilder();
                text.AppendLine(_meetingCount>0?"Reuniao ativa: "+_topic:"Nenhuma reuniao ativa.");
                foreach(var member in Members) text.AppendLine(member.Name+": "+Activities[member.Role].Stage);
                if(!string.IsNullOrEmpty(_report)) text.AppendLine("Ultimo relato consolidado: "+_report);
                return text.ToString();
            }
        }
        public static TeamMember Member(TeamRole role) { return Members.First(x=>x.Role==role); }
        public static void SetTechnicalRoute(string bucket,string model)
        {
            lock(Sync) {var member=Member(TeamRole.Technician);member.Bucket=bucket;member.Model=model;}
        }
        public static void SetResearchRoute(string bucket,string model)
        {
            lock(Sync) {var member=Member(TeamRole.Assistant);member.Bucket=bucket;member.Model=model;}
        }
        public static void SetWhatsAppRoute(string bucket,string model)
        {lock(Sync){var member=Member(TeamRole.WhatsAppResearcher);member.Bucket=bucket;member.Model=model;}}
        public static bool IsTeamRequest(string text)
        {
            return FridaTaskRouting.Decide(text,false,false).NeedsTeam;
        }
        public static void BeginMeeting(string topic, FridaSettings settings)
        {
            lock(Sync)
            {
                if (_meetingCount > 0) throw new InvalidOperationException("Ja existe uma reuniao em andamento.");
                _meetingCount=1; _topic=topic; _report=""; Messages.Clear(); Participants.Clear();
                foreach(var member in Members)
                    if (member.Role!=TeamRole.Hearing && member.Role!=TeamRole.Voice && member.HasKey(settings)) Participants.Add(member.Role);
            }
        }
        public static void EndMeeting()
        {
            lock(Sync)
            {
                _meetingCount=0; Participants.Clear();
                foreach(var a in Activities.Values) { if(a.Meeting) {a.Busy=false; a.Meeting=false; a.LastUtc=DateTime.UtcNow; a.Stage="Disponivel no hall";} }
            }
        }
        public static void BeginWork(TeamRole role, string stage, bool meeting)
        {
            lock(Sync) { var a=Activities[role]; a.Busy=true; a.Meeting=meeting; a.Stage=stage; a.LastUtc=DateTime.UtcNow; }
        }
        public static void EndWork(TeamRole role)
        {
            lock(Sync) {var a=Activities[role];a.Busy=false;a.Meeting=false;a.LastUtc=DateTime.UtcNow;a.Stage="Disponivel no hall";}
        }
        public static void Add(TeamRole role,string model,string text,string recipients,bool success)
        {
            lock(Sync)
            {
                Messages.Add(new TeamMessage {Role=role,Title=Member(role).Name,Model=model,Text=text ?? "",Recipients=recipients,Success=success,AtUtc=DateTime.UtcNow});
                if(Messages.Count>80) Messages.RemoveAt(0);
            }
        }
        public static List<TeamMessage> Transcript() { lock(Sync) return new List<TeamMessage>(Messages); }
        public static void SetReport(string value) { lock(Sync) _report=value ?? ""; }
        public static DashboardRoom? Classify(bool disabled,bool cooldown,bool quota,bool busy,bool meeting,DateTime lastUtc,DateTime nowUtc)
        {
            if(disabled) return null;
            if(cooldown && quota) return DashboardRoom.BreakRoom;
            if(cooldown) return null;
            if(meeting) return DashboardRoom.MeetingRoom;
            if(busy) return DashboardRoom.Office;
            return (nowUtc-lastUtc).TotalSeconds>=IdleSeconds ? DashboardRoom.GameRoom : DashboardRoom.Hall;
        }
        public static List<ModelRuntimeStats> DashboardStats(FridaSettings settings)
        {
            var raw=ModelQuotaGuard.GetAllStats();
            var result=new List<ModelRuntimeStats>();
            lock(Sync)
            {
                // A voz exibida e a voz principal real: Edge (gratis) no modo economico, Aoede (paga) fora dele.
                var voice=Member(TeamRole.Voice);
                bool edge=settings!=null && settings.prefer_zero_quota_tts;
                voice.Bucket=edge?"tts_edge":"tts";
                voice.Model=edge?ModelSelection.EdgeSpeech:ModelSelection.Speech;
                voice.Name=edge?"Voz principal / Edge "+ModelSelection.EdgeSpeechName:"Voz principal / Aoede";
                foreach(var member in Members)
                {
                    if(!member.HasKey(settings)) continue;
                    var source=raw.FirstOrDefault(x=>x.Bucket==member.Bucket && x.ModelId==member.Model);
                    if(source==null) source=ModelQuotaGuard.EnsureRegistered(member.Bucket,member.Model);
                    if(source==null) continue;
                    var a=Activities[member.Role];
                    bool transport=member.Role==TeamRole.Hearing || member.Role==TeamRole.Voice;
                    var last=transport && source.LastUsedLocal!=DateTime.MinValue && source.LastUsedLocal.ToUniversalTime()>a.LastUtc ? source.LastUsedLocal.ToUniversalTime() : a.LastUtc;
                    bool busy=a.Busy || (transport && source.IsActiveNow);
                    bool inMeeting=_meetingCount>0 && Participants.Contains(member.Role) && (a.Meeting || !a.Busy);
                    bool quota=source.IsCooldownActive() && (source.LastErrorReason ?? "").IndexOf("429",StringComparison.Ordinal)>=0;
                    var item=new ModelRuntimeStats {
                        Bucket=member.Bucket,ModelId=member.Model,DashboardIdentity="team:"+member.Role,
                        TeamRoleId=member.Role.ToString(),TeamStage=busy?a.Stage:(inMeeting?"Acompanhando a reuniao":"Disponivel"),
                        FriendlyName=member.Name,CharacterTitle=member.Name,CharacterRole=member.Name,SpriteKey=member.Sprite,
                        LastUsedLocal=source.LastUsedLocal,IsActiveNow=busy,CooldownUntilUtc=source.CooldownUntilUtc,
                        LastErrorReason=source.LastErrorReason,LastErrorLocal=source.LastErrorLocal,
                        TotalRequestsToday=source.TotalRequestsToday,FailuresToday=source.FailuresToday,RateLimitedToday=source.RateLimitedToday,
                        TokensInToday=source.TokensInToday,TokensOutToday=source.TokensOutToday,
                        TotalRequestsAllTime=source.TotalRequestsAllTime,FailuresAllTime=source.FailuresAllTime,
                        TokensInAllTime=source.TokensInAllTime,TokensOutAllTime=source.TokensOutAllTime,
                        LastLatencyMs=source.LastLatencyMs,AvgLatencyMs=source.AvgLatencyMs,
                        QuotaRefreshPending=source.QuotaRefreshPending,ProviderLimitNote=source.ProviderLimitNote,RateLimits=source.RateLimits,RateLimitsAtLocal=source.RateLimitsAtLocal,
                        RequestHistoryUtc=source.RequestHistoryUtc,History=source.History
                    };
                    item.TeamLocation=Classify(source.IsDisabled,source.IsCooldownActive(),quota,busy,inMeeting,last,DateTime.UtcNow);
                    result.Add(item);
                }
            }
            return result;
        }
    }

    public sealed class FridaTeamClient
    {
        private readonly FridaSettings _settings;
        private readonly HttpClient _http;
        public FridaTeamClient(FridaSettings settings,HttpClient http) {_settings=settings;_http=http;}
        public async Task<string> QueryAsync(TeamRole role,string prompt)
        {return await QueryCoreAsync(role,prompt,2048,50);}
        /// <summary>Modelo que respondeu a ultima triagem (ex.: "oai_groq:openai/gpt-oss-120b").</summary>
        public string LastEconomyRoute { get; private set; }

        // Triagem economica: Groq e Cerebras (gratuitos) na linha de frente; Gemini Lite (pago) so como reserva.
        public async Task<string> QueryEconomyAsync(string prompt,int outputTokens=FridaEconomyRouting.OutputTokens,int timeoutSeconds=FridaEconomyRouting.TimeoutSeconds)
        {
            LastEconomyRoute=null;
            var routes=new[]{
                new KeyValuePair<string,string>("oai_groq",ModelSelection.GroqBrain),
                new KeyValuePair<string,string>("oai_cerebras",ModelSelection.CerebrasBrain),
                new KeyValuePair<string,string>("gemini",ModelSelection.GeminiLite)};
            try
            {
                foreach(var route in routes)
                {
                    FridaWorkContext.ThrowIfCancelled();
                    FridaTeamState.SetResearchRoute(route.Key,route.Value);
                    string answer=await QueryCoreAsync(TeamRole.Assistant,prompt,outputTokens,timeoutSeconds,true).ConfigureAwait(false);
                    if(route.Key!="gemini" && !FridaEconomyRouting.LooksLikeDecision(answer)) continue;
                    if(!string.IsNullOrWhiteSpace(answer)){LastEconomyRoute=route.Key+":"+route.Value;return answer;}
                }
                return null;
            }
            finally
            {
                // O estagiario permanece no provedor que respondeu, para o painel mostrar o consumo real dele.
                int sep=LastEconomyRoute==null?-1:LastEconomyRoute.IndexOf(':');
                if(sep>0)FridaTeamState.SetResearchRoute(LastEconomyRoute.Substring(0,sep),LastEconomyRoute.Substring(sep+1));
                else FridaTeamState.SetResearchRoute("gemini",ModelSelection.GeminiLite);
            }
        }
        private Task<string> QueryCoreAsync(TeamRole role,string prompt,int outputTokens,int timeoutSeconds)
        {return QueryCoreAsync(role,prompt,outputTokens,timeoutSeconds,false);}
        private async Task<string> QueryCoreAsync(TeamRole role,string prompt,int outputTokens,int timeoutSeconds,bool economy)
        {
            FridaWorkContext.ThrowIfCancelled();
            var member=FridaTeamState.Member(role);
            if(role==TeamRole.Assistant && member.Bucket=="gsearch")FridaTeamState.SetResearchRoute("gemini",member.Model);
            if(!member.HasKey(_settings) || !ModelQuotaGuard.IsAvailable(member.Bucket,member.Model)) return null;
            var json=new JavaScriptSerializer {MaxJsonLength=8*1024*1024};
            string endpoint,key;
            object payload;
            bool google=member.Bucket=="gemini";
            string sanitizedPrompt = FridaInteractionPolicy.SanitizeSecurityPromptForCloudClassifier(prompt);
            if(google)
            {
                endpoint="https://generativelanguage.googleapis.com/v1beta/models/"+member.Model+":generateContent";
                key=(_settings.api_key ?? "").Split(new char[]{',',';','\r','\n'},StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                var safetySettings = new[]
                {
                    new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "BLOCK_NONE" },
                    new { category = "HARM_CATEGORY_CIVIC_INTEGRITY", threshold = "BLOCK_NONE" }
                };
                payload=new {
                    contents=new[]{new {role="user",parts=new[]{new {text=sanitizedPrompt}}}},
                    system_instruction=new { parts = new[] { new { text = FridaInteractionPolicy.LocalDateContext() + FridaInteractionPolicy.UniversalUnrestrictedSystemPrompt } } },
                    safetySettings=safetySettings,
                    generationConfig=new {maxOutputTokens=outputTokens}
                };
            }
            else
            {
                endpoint=member.Bucket=="oai_cerebras"?"https://api.cerebras.ai/v1/chat/completions":"https://api.groq.com/openai/v1/chat/completions";
                key=member.Bucket=="oai_cerebras"?_settings.cerebras_api_key:_settings.groq_api_key;
                if(economy)
                    // gpt-oss conta o raciocinio no limite de saida: esforco baixo + folga evitam resposta vazia.
                    payload=new {
                        model=member.Model,
                        messages=new object[]{
                            new {role="system", content=FridaInteractionPolicy.LocalDateContext() + FridaInteractionPolicy.UniversalUnrestrictedSystemPrompt + "\nPapel da equipe: " + member.Name},
                            new {role="user", content=sanitizedPrompt}
                        },
                        max_completion_tokens=Math.Max(1024,outputTokens+768),
                        reasoning_effort="low",
                        temperature=0.3
                    };
                else
                payload=new {
                    model=member.Model,
                    messages=new object[]{
                        new {role="system", content=FridaInteractionPolicy.LocalDateContext() + FridaInteractionPolicy.UniversalUnrestrictedSystemPrompt + "\nPapel da equipe: " + member.Name},
                        new {role="user", content=sanitizedPrompt}
                    },
                    max_completion_tokens=2048,
                    temperature=0.3
                };
            }
            var watch=System.Diagnostics.Stopwatch.StartNew();
            ModelQuotaGuard.SetActiveModel(member.Bucket,member.Model);
            try
            {
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(FridaWorkContext.Token))
                using(var request=new HttpRequestMessage(HttpMethod.Post,endpoint))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    if(google) request.Headers.Add("x-goog-api-key",key.Trim());
                    else request.Headers.TryAddWithoutValidation("Authorization","Bearer "+key.Trim());
                    request.Content=new StringContent(json.Serialize(payload),Encoding.UTF8,"application/json");
                    using(var response=await _http.SendAsync(request,timeout.Token).ConfigureAwait(false))
                    {
                        string body=await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        ModelQuotaGuard.RecordHttpResponse(member.Bucket,member.Model,watch.ElapsedMilliseconds,response,body);
                        if(!response.IsSuccessStatusCode) return null;
                        var root=json.Deserialize<Dictionary<string,object>>(body);
                        if(google)
                        {
                            var candidates=root.ContainsKey("candidates")?root["candidates"] as ArrayList:null;
                            if(candidates==null || candidates.Count==0) return null;
                            var candidate=candidates[0] as Dictionary<string,object>;
                            if(candidate!=null && candidate.ContainsKey("finishReason") && Convert.ToString(candidate["finishReason"])=="MAX_TOKENS")return null;
                            var content=candidate!=null && candidate.ContainsKey("content")?candidate["content"] as Dictionary<string,object>:null;
                            var parts=content!=null && content.ContainsKey("parts")?content["parts"] as ArrayList:null;
                            var text=new StringBuilder();
                            if(parts!=null) foreach(Dictionary<string,object> part in parts)
                                if(part.ContainsKey("text") && (!part.ContainsKey("thought") || !object.Equals(part["thought"],true))) text.AppendLine(Convert.ToString(part["text"]));
                            return text.ToString().Trim();
                        }
                        var choices=root.ContainsKey("choices")?root["choices"] as ArrayList:null;
                        if(choices==null || choices.Count==0) return null;
                        var choice=choices[0] as Dictionary<string,object>;
                        var msg=choice!=null && choice.ContainsKey("message")?choice["message"] as Dictionary<string,object>:null;
                        string oaiResp = msg!=null && msg.ContainsKey("content")?Convert.ToString(msg["content"]):null;
                        if(FridaInteractionPolicy.IsCannedModelRefusal(oaiResp)) return null;
                        return oaiResp;
                    }
                }
            }
            catch(OperationCanceledException) {FridaWorkContext.ThrowIfCancelled();return null;}
            catch(Exception) {ModelQuotaGuard.RecordException(member.Bucket,member.Model,"Falha na consulta da equipe");return null;}
            finally {ModelQuotaGuard.ClearActiveModel(member.Bucket,member.Model);}
        }
    }

    public sealed class FridaTeamCoordinator
    {
        private readonly FridaSettings _settings;
        private readonly Func<TeamRole,string,Task<string>> _query;
        private readonly SemaphoreSlim _gate=new SemaphoreSlim(1,1);
        public FridaTeamCoordinator(FridaSettings settings,Func<TeamRole,string,Task<string>> query) {_settings=settings;_query=query;}
        private static string Limit(string value) {value=value ?? "";return value.Length>14000?value.Substring(0,14000):value;}
        private async Task<string> Ask(TeamRole role,string prompt,string recipients)
        {
            FridaWorkContext.ThrowIfCancelled();
            FridaTeamState.BeginWork(role,recipients,true);
            string answer=null;
            try {
                answer=await _query(role,FridaInteractionPolicy.Instructions+FridaReliability.Instructions+"\nPapel: "+FridaTeamState.Member(role).Name+". Responda em portugues. Registre decisoes e evidencias, sem expor raciocinio privado. Nao invente fontes, execucoes ou testes.\n"+prompt).ConfigureAwait(false);
            }
            catch(OperationCanceledException){throw;}
            catch(Exception) {answer=null;}
            finally {FridaTeamState.EndWork(role);}
            string record=string.IsNullOrWhiteSpace(answer)?"Indisponivel nesta etapa. Nenhuma resposta recebida.":answer;
            FridaTeamState.Add(role,FridaTeamState.Member(role).Model,record,recipients,!string.IsNullOrWhiteSpace(answer));
            return answer;
        }
        private static async Task Progress(Func<string,Task> callback,string text)
        {
            FridaWorkContext.ThrowIfCancelled();
            if(callback==null) return;
            FridaTeamState.BeginWork(TeamRole.Secretary,"Informando o usuario",false);
            try {await callback(text).ConfigureAwait(false);} finally {FridaTeamState.EndWork(TeamRole.Secretary);}
        }
        public Task<string> RunAsync(string request,Func<string,Task<string>> execute,Func<string> evidence,Func<string,Task<string>> research,Func<string,Task> progress)
        { return RunAsync(request,execute,evidence,research,progress,null); }

        public async Task<string> RunAsync(string request,Func<string,Task<string>> execute,Func<string> evidence,Func<string,Task<string>> research,Func<string,Task> progress,FridaTaskPlan routing)
        {
            if(!FridaInteractionPolicy.IsRequest(request)) return "";
            if(!await _gate.WaitAsync(0).ConfigureAwait(false)) return "A equipe ja esta trabalhando em outra solicitacao. Aguarde a conclusao para iniciar uma nova reuniao.";
            bool begun=false;
            try
            {
                routing=routing ?? FridaTaskRouting.Decide(request,false,true);
                FridaTeamState.BeginMeeting(request,_settings);begun=true;
                await Progress(progress,"Planejando a solicitacao.").ConfigureAwait(false);
                string plan=await Ask(TeamRole.Manager,"Solicitacao do usuario:\n"+Limit(request)+"\nDefina um plano curto, criterios verificaveis de conclusao e a delegacao ao tecnico e ao analista. Nao afirme que ja executou.\n"+routing.Instructions+"\nResponda em JSON com campos plan e research_queries (lista de consultas objetivas quando forem necessarias). Evite dados privados ou caminhos locais desnecessarios nas consultas. Pesquisa profunda: ate 4 consultas de aspectos diferentes. Revisao normativa: ate 2 consultas sobre normas e edicoes aplicaveis.","Tecnico e analista").ConfigureAwait(false);
                string sources="Nao foi solicitada consulta externa nesta etapa.";
                bool needsResearch=routing.ResearchOnly || routing.NormativeReview || System.Text.RegularExpressions.Regex.IsMatch(request ?? "",@"(?i)pesquis|documenta|norma|pre[cç]o|atualiz|fonte|compare");
                if(needsResearch && research!=null)
                {
                    FridaTeamState.BeginWork(TeamRole.Assistant,"Executando pesquisas planejadas",true);
                    var collected=new StringBuilder();
                    try
                    {
                        int maximum=routing.Kind==FridaTaskKind.DeepResearch?4:(routing.NormativeReview?2:1);
                        foreach(string query in FridaTaskRouting.ResearchQueries(request,plan,maximum,routing.Kind==FridaTaskKind.DeepResearch))
                        {
                            string found=await research(query).ConfigureAwait(false);
                            collected.AppendLine("Consulta: "+query+"\nResultado consultado: "+Limit(found));
                        }
                        sources=collected.ToString();
                    }
                    catch(OperationCanceledException){throw;}
                    catch(Exception) {sources=collected+"\nA pesquisa externa falhou. Fontes pendentes de verificacao.";}
                    finally {FridaTeamState.EndWork(TeamRole.Assistant);}
                }
                string support=await Ask(TeamRole.Assistant,"Pedido:\n"+Limit(request)+"\nPlano do gestor:\n"+Limit(plan)+"\nFontes efetivamente consultadas:\n"+Limit(sources)+"\nPrepare para o ANALISTA uma lista curta de fatos, fontes disponiveis e verificacoes pendentes. Sem fontes, declare a limitacao.","Analista").ConfigureAwait(false);
                await Progress(progress,string.IsNullOrWhiteSpace(plan)?"Gestor indisponivel. Tentando atendimento tecnico.":"Executando o pedido e verificando os retornos.").ConfigureAwait(false);
                string result;
                FridaTeamState.BeginWork(TeamRole.Technician,"Executando a solicitacao do gestor",false);
                try {result=await execute(request+"\n\n[ORIENTACAO DA EQUIPE]\nPlano do gestor (pode conter limitacoes):\n"+Limit(plan)+"\nApoio destinado ao analista:\n"+Limit(support)+"\nExecute apenas o escopo do pedido original. Use ferramentas para verificar resultados e relate falhas reais.").ConfigureAwait(false);}
                catch(OperationCanceledException){throw;}
                catch(Exception) {result="A execucao tecnica falhou. A tarefa nao pode ser considerada concluida.";}
                finally {FridaTeamState.EndWork(TeamRole.Technician);}
                string observed=evidence==null?"Sem evidencias de ferramentas.":Limit(evidence());
                FridaTeamState.Add(TeamRole.Technician,FridaTeamState.Member(TeamRole.Technician).Model,result,"Gestor e analista",!string.IsNullOrWhiteSpace(result));
                string audit=await Ask(TeamRole.Analyst,"Pedido:\n"+Limit(request)+"\nPlano:\n"+Limit(plan)+"\nApoio do assistente:\n"+Limit(support)+"\nRelato tecnico:\n"+Limit(result)+"\nEVIDENCIAS OBSERVADAS:\n"+observed+"\nCriterios especificos:\n"+routing.Instructions+"\nResponda ao GESTOR e ao TECNICO. Separe comprovado, falhas e pendencias. Uma resposta de modelo nao prova uma acao. Nao afirme que executou testes: voce apenas inspeciona estas evidencias.","Gestor e tecnico").ConfigureAwait(false);
                string decision=await Ask(TeamRole.Manager,"Pedido:\n"+Limit(request)+"\nRelato tecnico:\n"+Limit(result)+"\nAuditoria do analista:\n"+Limit(audit)+"\nEvidencias:\n"+observed+"\nDecida o estado do trabalho para a secretaria: feito, nao feito e pendente. Se auditoria indisponivel, declare que o resultado nao foi auditado. Nao invente conclusoes.","Secretaria Frida").ConfigureAwait(false);
                string summary=await Ask(TeamRole.Secretary,"Voce e a unica interlocutora com o operador: Frida, parceira real com voz natural feminina.\nPedido:\n"+Limit(request)+"\nDecisao do gestor:\n"+Limit(decision)+"\nRelato tecnico:\n"+Limit(result)+"\nAuditoria:\n"+Limit(audit)+"\nEvidencias reais observadas:\n"+observed+"\nProduza uma resposta falada clara e direta (1 a 4 frases). Cite evidencias concretas. ATENCAO: Se nao houver evidencia de arquivo criado ou comando executado, declare com honestidade que a acao fisica no disco nao foi concluida. NUNCA afirme que criou, salvou ou abriu um arquivo ou executou uma limpeza sem evidencia registrada. Nao dite caminhos de arquivos cruos com barras.","Operador").ConfigureAwait(false);
                if(string.IsNullOrWhiteSpace(summary)) summary="O tecnico relatou: "+Limit(result)+" A secretaria nao conseguiu consolidar a resposta. "+(string.IsNullOrWhiteSpace(audit)?"A auditoria ficou pendente.":"Auditoria: "+Limit(audit));
                else
                {
                    if(string.IsNullOrWhiteSpace(plan)) summary+=" O planejamento do gestor nao foi recebido.";
                    if(string.IsNullOrWhiteSpace(audit)) summary+=" A auditoria independente nao foi concluida.";
                    if(string.IsNullOrWhiteSpace(decision)) summary+=" A validacao final do gestor ficou pendente.";
                }
                FridaTeamState.SetReport(summary);
                return summary;
            }
            finally {if(begun) FridaTeamState.EndMeeting();_gate.Release();}
        }
    }
}
