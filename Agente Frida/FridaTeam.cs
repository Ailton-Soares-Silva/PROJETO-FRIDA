// Equipe hierarquica e telemetria de atividades reais, sem novos modelos.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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
                Bucket.Contains("groq") ? s.groq_api_key :
                (Bucket.Contains("kimi") || Bucket.Contains("moonshot")) ? s.kimi_api_key :
                Bucket.Contains("deepseek") ? s.deepseek_api_key : s.api_key;
            return !string.IsNullOrWhiteSpace(key);
        }
    }

    public sealed class TeamMessage
    {
        public string Id;
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
            new TeamMember {Role=TeamRole.Manager, Name="Gestor / Cerebro", Bucket="oai_deepseek", Model=ModelSelection.DeepSeekProModel, Sprite="scholar"},
            new TeamMember {Role=TeamRole.Technician, Name="Engenheiro / Kimi K3", Bucket="oai_kimi", Model=ModelSelection.KimiK3, Sprite="scout"},
            new TeamMember {Role=TeamRole.Analyst, Name="Analista / Auditor", Bucket="oai_cerebras", Model=ModelSelection.CerebrasBrain, Sprite="titan"},
            new TeamMember {Role=TeamRole.Assistant, Name="Estagiário / Pesquisa", Bucket="oai_deepseek", Model=ModelSelection.DeepSeekModel, Sprite="scribe"},
            new TeamMember {Role=TeamRole.WhatsAppResearcher, Name="Pesquisador sênior / DeepSeek R1", Bucket="oai_deepseek", Model=ModelSelection.DeepSeekR1, Sprite="scholar"},
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
        public static string NormalizeModelFamily(string model)
        {
            if(string.IsNullOrWhiteSpace(model))return "";
            string m=model.Trim().ToLowerInvariant();
            int slash=m.LastIndexOf('/');
            if(slash>=0 && slash+1<m.Length)m=m.Substring(slash+1);
            return m;
        }
        public static bool AreSameModelFamily(string modelA,string modelB)
        {
            string fa=NormalizeModelFamily(modelA);
            string fb=NormalizeModelFamily(modelB);
            return !string.IsNullOrEmpty(fa) && string.Equals(fa,fb,StringComparison.OrdinalIgnoreCase);
        }
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
        private static string _meetingArchiveId;
        private static string _meetingStartedUtc;
        private static string _meetingRequestId;
        private static FridaIdeProject _meetingProject;
        private static void PersistMeeting()
        {
            if (_meetingArchiveId == null) return;
            try
            {
                FridaIdeRuntime.SaveMeeting(new FridaIdeMeeting {
                    Id = _meetingArchiveId, ConversationId = FridaIdeRuntime.Context == null ? null : FridaIdeRuntime.Context.ConversationId, RequestId = _meetingRequestId, Project = _meetingProject, Topic = _topic, Report = _report,
                    StartedUtc = _meetingStartedUtc, FinishedUtc = _meetingCount == 0 ? DateTime.UtcNow.ToString("o") : null,
                    Messages = new List<TeamMessage>(Messages)
                });
            }
            catch (Exception ex) { BootLogger.Log("Falha ao salvar reunião: " + ex.GetType().Name); }
        }
        public static void BeginMeeting(string topic, FridaSettings settings)
        {
            lock(Sync)
            {
                if (_meetingCount > 0) throw new InvalidOperationException("Ja existe uma reuniao em andamento.");
                _meetingRequestId = FridaIdeRuntime.Context == null ? null : FridaIdeRuntime.Context.RequestId;
                _meetingProject = FridaIdeRuntime.Context == null ? null : FridaIdeRuntime.Context.Project;
                _meetingArchiveId = Guid.NewGuid().ToString("N"); _meetingStartedUtc = DateTime.UtcNow.ToString("o");
                _meetingCount=1; _topic=topic; _report=""; Messages.Clear(); Participants.Clear();
                foreach(var member in Members)
                    if (member.Role!=TeamRole.Hearing && member.Role!=TeamRole.Voice && member.HasKey(settings)) Participants.Add(member.Role);
                PersistMeeting();
            }
        }
        public static void EndMeeting()
        {
            lock(Sync)
            {
                _meetingCount=0; Participants.Clear();
                PersistMeeting();
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
                var contribution = new TeamMessage {Id=Guid.NewGuid().ToString("N"),Role=role,Title=Member(role).Name,Model=model,Text=text ?? "",Recipients=recipients,Success=success,AtUtc=DateTime.UtcNow};
                Messages.Add(contribution);
                FridaMeetingWorkspace.Message(contribution);
                PersistMeeting();
            }
        }
        public static List<TeamMessage> Transcript() { lock(Sync) return new List<TeamMessage>(Messages); }
        public static void SetReport(string value) { lock(Sync) { _report=value ?? ""; PersistMeeting();
            if (FridaIdeRuntime.Context != null && FridaIdeRuntime.Context.RequestId == _meetingRequestId) FridaMeetingWorkspace.Report(_report); } }
        public static DashboardRoom? Classify(bool disabled,bool cooldown,bool quota,bool busy,bool meeting,DateTime lastUtc,DateTime nowUtc)
        {
            if(disabled) return null;
            if(cooldown) return DashboardRoom.BreakRoom;
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
                Member(TeamRole.Hearing).Model=settings==null?ModelSelection.Transcription:FridaVoiceOptions.Primary(settings);
                var voice=Member(TeamRole.Voice);
                bool edge=settings!=null && settings.prefer_zero_quota_tts;
                voice.Bucket=edge?"tts_edge":"tts";
                voice.Model=edge?ModelSelection.EdgeSpeech:ModelSelection.Speech;
                voice.Name=edge?"Voz principal / Edge "+ModelSelection.EdgeSpeechName:"Voz principal / Aoede";

                var manager = Member(TeamRole.Manager);
                if (settings != null && !string.IsNullOrWhiteSpace(settings.deepseek_api_key))
                {
                    manager.Bucket = "oai_deepseek";
                    manager.Model = ModelSelection.DeepSeekProModel;
                    manager.Name = "Gestor / DeepSeek V4 Pro";
                    manager.Sprite = "scholar";
                }
                ModelQuotaGuard.EnsureRegistered(manager.Bucket, manager.Model);

                var assistant = Member(TeamRole.Assistant);
                if (settings != null && !string.IsNullOrWhiteSpace(settings.deepseek_api_key))
                {
                    assistant.Bucket = "oai_deepseek";
                    assistant.Model = ModelSelection.DeepSeekModel;
                    assistant.Name = "Estagiário / DeepSeek";
                    assistant.Sprite = "scribe";
                }
                ModelQuotaGuard.EnsureRegistered(assistant.Bucket, assistant.Model);

                var technician = Member(TeamRole.Technician);
                if (settings != null && !string.IsNullOrWhiteSpace(settings.kimi_api_key))
                {
                    technician.Bucket = "oai_kimi";
                    technician.Model = ModelSelection.KimiK3;
                    technician.Name = "Engenheiro / Kimi K3";
                    technician.Sprite = "scout";
                }
                ModelQuotaGuard.EnsureRegistered(technician.Bucket, technician.Model);

                var seniorResearcher = Member(TeamRole.WhatsAppResearcher);
                if (settings != null && !string.IsNullOrWhiteSpace(settings.deepseek_api_key))
                {
                    seniorResearcher.Bucket = "oai_deepseek";
                    seniorResearcher.Model = ModelSelection.DeepSeekR1;
                    seniorResearcher.Name = "Pesquisador sênior / DeepSeek R1";
                    seniorResearcher.Sprite = "scholar";
                }
                ModelQuotaGuard.EnsureRegistered(seniorResearcher.Bucket, seniorResearcher.Model);
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
        {
            int roleTokens = (role==TeamRole.Manager || role==TeamRole.Analyst)
                ? (_settings!=null && _settings.complex_work_max_tokens>0 ? _settings.complex_work_max_tokens : 32768)
                : (role==TeamRole.Technician || role==TeamRole.WhatsAppResearcher
                    ? (_settings!=null && _settings.implementation_max_tokens>0 ? _settings.implementation_max_tokens : 16384)
                    : 8192);
            string effort = (role==TeamRole.Manager || role==TeamRole.Analyst) ? "high"
                : (role==TeamRole.Technician || role==TeamRole.WhatsAppResearcher ? "medium" : "low");
            return await QueryCoreAsync(role,prompt,roleTokens,75,false,effort).ConfigureAwait(false);
        }
        /// <summary>Modelo que respondeu a ultima triagem (ex.: "oai_groq:openai/gpt-oss-120b").</summary>
        public string LastEconomyRoute { get; private set; }

        // Triagem economica: Groq e Cerebras (gratuitos) na linha de frente; Gemini Lite (pago) so como reserva.
        public async Task<string> QueryEconomyAsync(string prompt,int outputTokens=FridaEconomyRouting.OutputTokens,int timeoutSeconds=FridaEconomyRouting.TimeoutSeconds)
        {
            LastEconomyRoute=null;
            var routes=new[]{
                new KeyValuePair<string,string>("oai_deepseek",ModelSelection.DeepSeekModel),
                new KeyValuePair<string,string>("oai_groq",ModelSelection.GroqBrain),
                new KeyValuePair<string,string>("oai_cerebras",ModelSelection.CerebrasBrain),
                new KeyValuePair<string,string>("gemini",ModelSelection.GeminiLite)};
            try
            {
                foreach(var route in routes)
                {
                    FridaWorkContext.ThrowIfCancelled();
                    FridaTeamState.SetResearchRoute(route.Key,route.Value);
                    string answer=await QueryCoreAsync(TeamRole.Assistant,prompt,outputTokens,timeoutSeconds,true,"low").ConfigureAwait(false);
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
                else if(_settings!=null && !string.IsNullOrWhiteSpace(_settings.deepseek_api_key))
                    FridaTeamState.SetResearchRoute("oai_deepseek",ModelSelection.DeepSeekModel);
                else FridaTeamState.SetResearchRoute("oai_groq",ModelSelection.GroqBrain);
            }
        }
        private Task<string> QueryCoreAsync(TeamRole role,string prompt,int outputTokens,int timeoutSeconds)
        {return QueryCoreAsync(role,prompt,outputTokens,timeoutSeconds,false,null,false);}
        private async Task<string> QueryCoreAsync(TeamRole role,string prompt,int outputTokens,int timeoutSeconds,bool economy,string reasoningEffort=null,bool isRetry=false)
        {
            FridaWorkContext.ThrowIfCancelled();
            var member=FridaTeamState.Member(role);
            if(role==TeamRole.Assistant && member.Bucket=="gsearch")FridaTeamState.SetResearchRoute("gemini",member.Model);
            if(!member.HasKey(_settings) || !ModelQuotaGuard.IsAvailable(member.Bucket,member.Model))
            {
                var fallback = FridaModelCatalog.ResolveNextFallback(role, member.Model, _settings);
                if (fallback != null)
                {
                    member = new TeamMember
                    {
                        Role = role,
                        Name = member.Name + " (" + fallback.DisplayName + ")",
                        Bucket = fallback.ProviderBucket,
                        Model = fallback.ModelId,
                        Sprite = member.Sprite
                    };
                }
                else return null;
            }
            var json=new JavaScriptSerializer {MaxJsonLength=8*1024*1024};
            string endpoint,key;
            object payload;
            bool google=member.Bucket=="gemini";
            string sanitizedPrompt = FridaInteractionPolicy.SanitizeSecurityPromptForCloudClassifier(prompt);
            string effort = economy ? "low" : (string.IsNullOrWhiteSpace(reasoningEffort)
                ? ((role==TeamRole.Manager || role==TeamRole.Analyst) ? "high" : (role==TeamRole.Technician ? "medium" : "low"))
                : reasoningEffort.Trim().ToLowerInvariant());
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
                    generationConfig=new {maxOutputTokens=Math.Max(1024,Math.Min(outputTokens,32768))}
                };
            }
            else
            {
                bool isDeepSeek = member.Bucket.Contains("deepseek");
                bool isKimi = member.Bucket.Contains("kimi") || member.Bucket.Contains("moonshot");
                endpoint=isDeepSeek ? "https://api.deepseek.com/chat/completions" : (isKimi ? "https://api.moonshot.ai/v1/chat/completions" : (member.Bucket=="oai_cerebras"?"https://api.cerebras.ai/v1/chat/completions":"https://api.groq.com/openai/v1/chat/completions"));
                key=isDeepSeek ? _settings.deepseek_api_key : (isKimi ? _settings.kimi_api_key : (member.Bucket=="oai_cerebras"?_settings.cerebras_api_key:_settings.groq_api_key));
                int oaiTokens = economy ? Math.Max(2048,outputTokens+768) : Math.Max(4096,Math.Min(outputTokens,32768));
                payload = isKimi
                    ? (object)new {
                        model=member.Model,
                        messages=new object[]{
                            new {role="system", content=FridaInteractionPolicy.LocalDateContext() + FridaInteractionPolicy.UniversalUnrestrictedSystemPrompt + "\nPapel da equipe: " + member.Name},
                            new {role="user", content=sanitizedPrompt}
                        },
                        max_tokens=oaiTokens,
                        temperature=1.0
                    }
                    : (isDeepSeek
                        ? (object)new {
                            model=member.Model,
                            messages=new object[]{
                                new {role="system", content=FridaInteractionPolicy.LocalDateContext() + FridaInteractionPolicy.UniversalUnrestrictedSystemPrompt + "\nPapel da equipe: " + member.Name},
                                new {role="user", content=sanitizedPrompt}
                            },
                            max_tokens=oaiTokens,
                            temperature=0.3
                        }
                        : (object)new {
                            model=member.Model,
                            messages=new object[]{
                                new {role="system", content=FridaInteractionPolicy.LocalDateContext() + FridaInteractionPolicy.UniversalUnrestrictedSystemPrompt + "\nPapel da equipe: " + member.Name},
                                new {role="user", content=sanitizedPrompt}
                            },
                            max_completion_tokens=oaiTokens,
                            reasoning_effort=effort,
                            temperature=0.3
                        });
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
                        if(!response.IsSuccessStatusCode)
                        {
                            if(!isRetry)
                            {
                                var fallback = FridaModelCatalog.ResolveNextFallback(role, member.Model, _settings);
                                if (fallback != null)
                                {
                                    BootLogger.Log(member.Name + " falhou (" + response.StatusCode + "); acionando reserva " + fallback.DisplayName + ".");
                                    var currentMember = FridaTeamState.Member(role);
                                    currentMember.Bucket = fallback.ProviderBucket;
                                    currentMember.Model = fallback.ModelId;
                                    return await QueryCoreAsync(role, prompt, outputTokens, timeoutSeconds, economy, reasoningEffort, true).ConfigureAwait(false);
                                }
                            }
                            return null;
                        }
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
        private static string Limit(string value) {value=value ?? "";return value.Length>24000?value.Substring(0,24000):value;}

        public static string SanitizeSecretaryOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string clean = text;
            clean = Regex.Replace(clean, @"(?i)\[stderr\]:[\s\S]*?(?=(\r?\n\r?\n|$))", "");
            clean = Regex.Replace(clean, @"(?i)(?:codigo de saida|c[oó]digo de sa[í]da):\s*\d+", "");
            clean = Regex.Replace(clean, @"(?i)processo encerrado com c[oó]digo zero\.\s*o resultado funcional ainda precisa de verifica[cç][aã]o\.?", "");
            clean = Regex.Replace(clean, @"(?i)arquivo gravado e conferido por releitura e sha-256:[^\r\n]+", "");
            clean = Regex.Replace(clean, @"(?i)\b(?:temp_\w+|extract_\w+)\.py\b", "");
            clean = Regex.Replace(clean, @"[\r\n]{3,}", "\r\n\r\n").Trim();
            return clean;
        }

        public static bool AuditRequiresCorrection(string auditText,string observedEvidence)
        {
            string a = auditText ?? "";
            string o = observedEvidence ?? "";
            if(a.IndexOf("[APROVADO_SEM_PENDENCIAS]",StringComparison.OrdinalIgnoreCase)>=0
                && o.IndexOf("compilacao_aprovada=NAO",StringComparison.OrdinalIgnoreCase)<0
                && o.IndexOf("testes_aprovados=NAO",StringComparison.OrdinalIgnoreCase)<0
                && o.IndexOf("build_passed=False",StringComparison.OrdinalIgnoreCase)<0
                && o.IndexOf("tests_passed=False",StringComparison.OrdinalIgnoreCase)<0)
            {
                return false;
            }
            if(a.IndexOf("[REPROVAR_CORRECAO_NECESSARIA]",StringComparison.OrdinalIgnoreCase)>=0
                || a.IndexOf("CORRECAO_OBRIGATORIA:",StringComparison.OrdinalIgnoreCase)>=0
                || a.IndexOf("DEFEITO_ENCONTRADO:",StringComparison.OrdinalIgnoreCase)>=0)
            {
                return true;
            }
            if(o.IndexOf("compilacao_aprovada=NAO",StringComparison.OrdinalIgnoreCase)>=0
                || o.IndexOf("testes_aprovados=NAO",StringComparison.OrdinalIgnoreCase)>=0
                || o.IndexOf("build_passed=False",StringComparison.OrdinalIgnoreCase)>=0
                || o.IndexOf("tests_passed=False",StringComparison.OrdinalIgnoreCase)>=0
                || o.IndexOf("Etapas pendentes:",StringComparison.OrdinalIgnoreCase)>=0
                || o.IndexOf("[FALHA]",StringComparison.OrdinalIgnoreCase)>=0)
            {
                return true;
            }
            return false;
        }

        // Same rule as the final report: approved by the manager, reviewed, and nothing left to correct or pending.
        internal static bool WorkApproved(string decision,string audit,string observed)
        {
            var report=FridaDecisionReports.Build(new FridaDecisionInput {Decision=decision,Audit=audit,Observed=observed,CorrectionRequired=AuditRequiresCorrection(audit,observed)});
            return report.Verdict==FridaTeamDecisionReport.Approved;
        }

        private async Task<string> Ask(TeamRole role,string prompt,string recipients)
        {
            FridaWorkContext.ThrowIfCancelled();
            FridaTeamState.BeginWork(role,recipients,true);
            string answer=null;
            try {
                string memCtx = FridaMemoryStore.GetRelevantMemoriesSummary(prompt);
                string sysHeader = FridaInteractionPolicy.Instructions + FridaReliability.Instructions
                    + "\nPapel: " + FridaTeamState.Member(role).Name
                    + ". Responda em portugues. Registre decisoes e evidencias, sem expor raciocinio privado. Nao invente fontes, execucoes ou testes."
                    + (string.IsNullOrEmpty(memCtx) ? "" : "\n" + memCtx)
                    + "\n";
                answer=await _query(role,sysHeader+prompt).ConfigureAwait(false);
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
                if(routing.Kind == FridaTaskKind.SkillEvolution)
                {
                    string evoSummary = await FridaSkillOrchestrator.EvolveSkillAsync(
                        request,
                        _settings,
                        (role, prompt) => Ask(role, prompt, "Equipe de Evolucao de Skills"),
                        execute,
                        progress).ConfigureAwait(false);
                    FridaTeamState.Add(TeamRole.Secretary, FridaTeamState.Member(TeamRole.Secretary).Model, evoSummary, "Operador", true);
                    return evoSummary;
                }
                await Progress(progress,"Planejando a solicitacao.").ConfigureAwait(false);
                string managerPrompt = "Solicitacao do operador:\n" + Limit(request)
                    + "\nDIRETRIZ DA ALCADA DO GESTOR (CEREBRO / DEEPSEEK V4 PRO):\n"
                    + "1. Assuma o comando e a visao geral da solicitacao.\n"
                    + "2. Analise o que o operador pediu, verifique os recursos disponiveis e identifique O QUE FALTA para cumprir o objetivo sem alucinacao.\n"
                    + "3. Se faltar contexto de como fazer, delegue pesquisa previa ao Pesquisador Estagiario (pesquisa web).\n"
                    + "4. Se faltar ferramentas ou dependencias, priorize instalar/preparar primeiro.\n"
                    + "5. Escale a alcada conforme a complexidade: Estagiario -> Analista -> Pesquisador Senior (DeepSeek R1 para analise/cruzamento de planilhas e WhatsApp) -> Engenheiro (Kimi K3 para codigo, ferramentas e execucao).\n"
                    + "6. Se for tarefa de planilhas e WhatsApp, instrua a consulta nas 3 planilhas base primeiro e o cruzamento dos dados com o WhatsApp em seguida.\n"
                    + "7. RECURSOS PYTHON E ARQUIVOS: Python 3.12 ('python.exe') esta disponivel no ambiente com PyPDF2, openpyxl, sqlite3 e urllib para construir qualquer funcionalidade necessaria. Para inspecionar documentos PDF (.pdf), Excel (.xlsx/.csv) ou Word (.docx), instrua SEMPRE o uso de manage_files read. Para calculos, transformacao de dados ou automacoes especializadas, instrua a execucao de scripts Python.\n"
                    + routing.Instructions
                    + "\nResponda em JSON com os campos: plan, missing_requirements, delegated_roles, acceptance_criteria, research_queries, workflow (passos do fluxo acordado) e proposed_skills (opcional: lista de {name, purpose} para skills sugeridas; propor nao registra a skill).";
                string plan=await Ask(TeamRole.Manager, managerPrompt, "Equipe Frida").ConfigureAwait(false);
                FridaMeetingWorkspace.Plan(plan);
                string sources="Nao foi solicitada consulta externa nesta etapa.";
                bool researched=false; // at least one planned query actually returned something
                bool needsResearch=routing.NeedsWeb
                    || routing.ResearchOnly
                    || routing.NormativeReview
                    || (routing.Steps != null && routing.Steps.Any(s => s.Role == "assistant" || s.Role == "research" || s.ToolHint == "web_research" || s.ToolHint == "web_search" || s.ToolHint == "fetch_url"))
                    || FridaTaskRouting.ResearchQueries(request, plan, 1, false).Count > 0;
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
                            if(!string.IsNullOrWhiteSpace(found) && !found.StartsWith("Nao foi possivel",StringComparison.OrdinalIgnoreCase) && !found.StartsWith("Pesquisa bloqueada",StringComparison.OrdinalIgnoreCase))researched=true;
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
                try {result=await execute(request+"\n\n[ORIENTACAO DA EQUIPE]\nPlano do gestor:\n"+Limit(plan)+"\nApoio destinado ao analista:\n"+Limit(support)+"\nSe for tarefa de desenvolvimento/correcao de software, siga: inspecionar projeto -> alterar/criar codigo -> compilar -> executar testes funcionais -> conferir recibos reais.\n[RECURSOS PYTHON E ARQUIVOS]: Para ler PDF, Excel (.xlsx/.csv) ou Word (.docx), use SEMPRE manage_files com operation='read' (extracao nativa de texto). NUNCA leia bytes brutos e NUNCA abra arquivos na tela do operador com Start-Process para inspecionar com inspect_screen. Para trazer funcionalidades adicionais necessarias, crie e execute scripts Python usando python.exe.").ConfigureAwait(false);}
                catch(OperationCanceledException){throw;}
                catch(Exception) {result="A execucao tecnica falhou. A tarefa nao pode ser considerada concluida.";}
                finally {FridaTeamState.EndWork(TeamRole.Technician);}
                string observed=evidence==null?"Sem evidencias de ferramentas.":Limit(evidence());
                FridaTeamState.Add(TeamRole.Technician,FridaTeamState.Member(TeamRole.Technician).Model,result,"Gestor e analista",!string.IsNullOrWhiteSpace(result));

                bool needsSeniorResearch = routing.NeedsLocalData
                    || (routing.Steps != null && routing.Steps.Any(s => s.Role == "senior_researcher" || s.Role == "whatsapp_researcher" || s.ToolHint == "read_whatsapp" || s.ToolHint == "cross_analysis"))
                    || System.Text.RegularExpressions.Regex.IsMatch(request ?? "", @"(?i)pesquisador\s+s[eê]nior|cruzamento\s+anal[ií]tico");
                if (needsSeniorResearch)
                {
                    FridaTeamState.BeginWork(TeamRole.WhatsAppResearcher, "Pesquisador Sênior: Cruzamento Analítico", false);
                    try
                    {
                        string seniorAnalysis = await Ask(TeamRole.WhatsAppResearcher,
                            "Solicitacao do operador:\n" + Limit(request)
                            + "\nPlano do gestor:\n" + Limit(plan)
                            + "\nRetorno das ferramentas:\n" + Limit(result)
                            + "\nEvidencias observadas:\n" + observed
                            + "\nVoce e o Pesquisador Senior (DeepSeek R1). Realize o cruzamento analitico dos dados (planilhas base vs WhatsApp), verifique correlacoes exatas de chassis, placas e pecas, e aponte com rigor os fatos confirmados e o que ficou pendente ou requer autorizacao.",
                            "Gestor e Analista").ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(seniorAnalysis))
                        {
                            result += "\n\n[CRUZAMENTO ANALITICO — PESQUISADOR SENIOR / DEEPSEEK R1]\n" + seniorAnalysis;
                            FridaTeamState.Add(TeamRole.WhatsAppResearcher, FridaTeamState.Member(TeamRole.WhatsAppResearcher).Model, seniorAnalysis, "Gestor e Analista", true);
                        }
                    }
                    catch (Exception ex) { BootLogger.Log("Senior research exception: " + ex.Message); }
                    finally { FridaTeamState.EndWork(TeamRole.WhatsAppResearcher); }
                }

                string skillCreated=null;

                string audit=await Ask(TeamRole.Analyst,"Pedido:\n"+Limit(request)+"\nPlano:\n"+Limit(plan)+"\nApoio do assistente:\n"+Limit(support)+"\nRelato tecnico:\n"+Limit(result)+"\nEVIDENCIAS OBSERVADAS (incluindo codigo alterado, diff e resultados de compilacao/testes):\n"+observed+"\nCriterios especificos:\n"+routing.Instructions+"\nResponda ao GESTOR e ao TECNICO. Separe comprovado, falhas e pendencias. Se houver erro de compilacao, teste falhando, etapa pendente ou defeito no codigo/diff, inclua a tag [REPROVAR_CORRECAO_NECESSARIA] e liste cada defeito como DEFEITO_ENCONTRADO: <descricao e correcao exata>. Se tudo estiver compilado, testado e comprovado sem defeitos, inclua [APROVADO_SEM_PENDENCIAS].","Gestor e tecnico").ConfigureAwait(false);

                int maxCycles = _settings!=null && _settings.max_correction_cycles>0 ? _settings.max_correction_cycles : 3;
                int cycle = 0;
                while(cycle < maxCycles && !routing.ResearchOnly && AuditRequiresCorrection(audit,observed))
                {
                    cycle++;
                    await Progress(progress,"Ciclo de correcao "+cycle+"/"+maxCycles+": corrigindo achados da revisao e revalidando.").ConfigureAwait(false);
                    FridaTeamState.BeginWork(TeamRole.Technician,"Ciclo de correcao "+cycle+"/"+maxCycles,false);
                    try
                    {
                        result = await execute(
                            request
                            + "\n\n[CICLO DE CORRECAO OBRIGATORIO " + cycle + "/" + maxCycles + "]\n"
                            + "Achados do Revisor/Analista que precisam ser resolvidos agora:\n" + Limit(audit) + "\n"
                            + "Evidencias e estado atual:\n" + observed + "\n"
                            + "Instrucoes de correcao: Aplique as correcoes necessarias no codigo (apply_patch ou manage_files), execute a compilacao e os testes funcionais (run_project_validation ou run_powershell) na nova revisao ate que build_passed e tests_passed estejam aprovados.").ConfigureAwait(false);
                    }
                    catch(OperationCanceledException){throw;}
                    catch(Exception)
                    {
                        result="Falha na tentativa de correcao "+cycle+"/"+maxCycles+".";
                    }
                    finally {FridaTeamState.EndWork(TeamRole.Technician);}

                    observed = evidence==null ? "Sem evidencias de ferramentas." : Limit(evidence());
                    FridaTeamState.Add(TeamRole.Technician,FridaTeamState.Member(TeamRole.Technician).Model,"[Ciclo "+cycle+"] "+result,"Gestor e analista",!string.IsNullOrWhiteSpace(result));

                    audit = await Ask(TeamRole.Analyst,
                        "Pedido:\n"+Limit(request)+"\nRelato tecnico apos ciclo de correcao "+cycle+"/"+maxCycles+":\n"+Limit(result)
                        +"\nEVIDENCIAS ATUALIZADAS (codigo, diff, compilacao e testes):\n"+observed
                        +"\nVerifique se todos os defeitos anteriores foram resolvidos na revisao atual. Se ainda houver falha ou pendencia, inclua [REPROVAR_CORRECAO_NECESSARIA] e detalhe o que resta. Se estiver 100% resolvido e validado, inclua [APROVADO_SEM_PENDENCIAS].","Gestor e tecnico").ConfigureAwait(false);
                }

                bool sameFamily = FridaTeamState.AreSameModelFamily(
                    FridaTeamState.Member(TeamRole.Technician).Model,
                    FridaTeamState.Member(TeamRole.Analyst).Model);
                string independenceNote = sameFamily
                    ? "Nota: Tecnico e Analista operam a mesma familia de modelo ("+FridaTeamState.NormalizeModelFamily(FridaTeamState.Member(TeamRole.Technician).Model)+") em provedores distintos; a garantia de correcao vem dos recibos verificados de compilacao e testes, nao de diversidade de modelo."
                    : "Revisao realizada entre familias de modelos distintas.";

                // Structured decision: the final report is built from these fields (and the receipts), never from free text alone.
                string decision=await Ask(TeamRole.Manager,"Pedido:\n"+Limit(request)+"\nCiclos de correcao executados: "+cycle+"/"+maxCycles+"\nRelato tecnico final:\n"+Limit(result)+"\nRevisao do analista:\n"+Limit(audit)+"\n"+independenceNote+"\nEvidencias:\n"+observed
                    +"\nDecida o estado do trabalho para a secretaria: feito, nao feito e pendente. Se revisao indisponivel, declare que o resultado nao foi revisado. Nao invente conclusoes."
                    +"\nResponda em JSON: {\"veredito\":\"aprovado | aprovado_com_pendencias | reprovado | nao_revisado\",\"feito\":[\"...\"],\"pendente\":[\"...\"],\"nao_feito\":[\"...\"],\"justificativa\":\"...\",\"proximos_passos\":[\"...\"],"
                    +"\"atualizar_skill\":{\"skill_id\":\"id de skill existente que esta rodada ensinou algo, ou vazio\",\"licoes\":[\"licao objetiva aprendida\"]}}"
                    +(FridaSkillRegistry.GetAll().Count>0?"\nSkills existentes: "+string.Join(", ",FridaSkillRegistry.GetAll().Select(s=>s.Id).Take(40).ToArray()):""),"Secretaria Frida").ConfigureAwait(false);
                // Skill learning managed by the Manager. It runs only after the work was approved without
                // pending items, and only when the run really researched something or built a new
                // procedure: a rejected or routine run teaches nothing worth keeping.
                bool newProcedure = researched || System.Text.RegularExpressions.Regex.IsMatch(request ?? "", @"(?i)\b(instal\w*|como\s+fazer|integr\w*|scripts?|automati\w*)\b");
                if (newProcedure && !routing.ResearchOnly && WorkApproved(decision, audit, observed))
                {
                    string learnedSummary = await FridaSkillOrchestrator.EvaluateAndRegisterLearnedSkillAsync(
                        request,
                        result,
                        sources,
                        _settings,
                        (role, prompt) => Ask(role, prompt, "Gestor: Aprendizado de Skills"),
                        progress).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(learnedSummary))
                    {
                        skillCreated = learnedSummary;
                        result += learnedSummary;
                        FridaTeamState.Add(TeamRole.Manager, FridaTeamState.Member(TeamRole.Manager).Model, learnedSummary, "Equipe Frida", true);
                    }
                }
                string summary=await Ask(TeamRole.Secretary,"Voce e a unica interlocutora com o operador: Frida, parceira real com voz natural feminina.\nPedido:\n"+Limit(request)+"\nDecisao do gestor:\n"+Limit(decision)+"\nRelato tecnico:\n"+Limit(result)+"\nRevisao:\n"+Limit(audit)+"\n"+independenceNote+"\nEvidencias reais observadas:\n"+observed+"\nProduza uma resposta falada clara e direta (1 a 4 frases). Cite evidencias concretas. ATENCAO: Se nao houver evidencia de arquivo criado, compilacao aprovada ou teste executado, declare com honestidade o que ficou pendente. NUNCA apresente dois provedores da mesma familia de modelo como garantia de revisao independente. Nao dite caminhos de arquivos cruos com barras.","Operador").ConfigureAwait(false);
                if(string.IsNullOrWhiteSpace(summary)) summary="O tecnico relatou: "+Limit(result)+" A secretaria nao conseguiu consolidar a resposta. "+(string.IsNullOrWhiteSpace(audit)?"A revisao tecnica ficou pendente.":"Revisao: "+Limit(audit));
                else
                {
                    if(string.IsNullOrWhiteSpace(plan)) summary+=" O planejamento do gestor nao foi recebido.";
                    if(string.IsNullOrWhiteSpace(audit)) summary+=" A revisao tecnica nao foi concluida.";
                    if(string.IsNullOrWhiteSpace(decision)) summary+=" A validacao final do gestor ficou pendente.";
                }
                summary = SanitizeSecretaryOutput(summary);
                FridaTeamState.SetReport(summary);
                // The secretary presents the final decision as a card in the chat with the detailed report
                // (evidence, screenshots, skill update). Reporting never fails the request itself.
                try { FridaDecisionReports.Publish(new FridaDecisionInput { Request=request, Plan=plan, Decision=decision, Audit=audit, Observed=observed, Summary=summary,
                    IndependenceNote=independenceNote, Cycles=cycle, MaxCycles=maxCycles, SkillCreated=skillCreated, CorrectionRequired=AuditRequiresCorrection(audit,observed) }); }
                catch(Exception ex) {BootLogger.Log("Relatorio de decisao nao publicado: "+ex.GetType().Name+" "+ex.Message);}
                return summary;
            }
            finally {if(begun) FridaTeamState.EndMeeting();_gate.Release();}
        }
    }

    // What the coordinator already holds when the secretary closes a run.
    public sealed class FridaDecisionInput
    {
        public string Request, Plan, Decision, Audit, Observed, Summary, IndependenceNote, SkillCreated;
        public int Cycles, MaxCycles;
        public bool CorrectionRequired;
    }
    public sealed class FridaDecisionEvidence { public string Ref, Tool, Status, Summary; }
    public sealed class FridaDecisionScreenshot { public string Path, Sha256, Caption, AtUtc; }

    // Screenshots kept as evidence of a request: saved in the conversation (Evidence\ev-NNN.jpg) with
    // their SHA-256, so the final report shows exactly what was seen. Stored locally only; they go to
    // the Trash with the conversation.
    public static class FridaEvidenceCapture
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, List<FridaDecisionScreenshot>> ByRequest = new Dictionary<string, List<FridaDecisionScreenshot>>();
        // Desktop capture (JPEG) provided by the agent; null where there is no screen to capture.
        public static Func<byte[]> Capture;
        public static FridaDecisionScreenshot Save(byte[] jpeg, string caption)
        {
            var context = FridaIdeRuntime.Context;
            if (jpeg == null || jpeg.Length == 0 || context == null || context.ConversationId == null) return null;
            try
            {
                string folder = System.IO.Path.Combine(FridaMeetingWorkspace.Store(context).Root, "Conversations", context.ConversationId, "Evidence");
                System.IO.Directory.CreateDirectory(folder);
                lock (Sync)
                {
                    int n = System.IO.Directory.GetFiles(folder, "ev-*.jpg").Length + 1;
                    string path = System.IO.Path.Combine(folder, "ev-" + n.ToString("D3") + ".jpg");
                    while (System.IO.File.Exists(path)) path = System.IO.Path.Combine(folder, "ev-" + (++n).ToString("D3") + ".jpg");
                    System.IO.File.WriteAllBytes(path, jpeg);
                    var shot = new FridaDecisionScreenshot { Path = path, Sha256 = FridaVerifiedTools.Sha256(jpeg), Caption = string.IsNullOrWhiteSpace(caption) ? "Tela" : caption.Trim(), AtUtc = DateTime.UtcNow.ToString("o") };
                    List<FridaDecisionScreenshot> list;
                    if (!ByRequest.TryGetValue(context.RequestId, out list)) ByRequest[context.RequestId] = list = new List<FridaDecisionScreenshot>();
                    list.Add(shot);
                    return shot;
                }
            }
            catch (Exception ex) { BootLogger.Log("Print de evidencia nao salvo: " + ex.GetType().Name); return null; }
        }
        public static FridaDecisionScreenshot CaptureNow(string caption)
        {
            var capture = Capture; byte[] jpeg = null;
            try { if (capture != null) jpeg = capture(); } catch (Exception ex) { BootLogger.Log("Captura de tela para evidencia falhou: " + ex.GetType().Name); }
            return Save(jpeg, caption);
        }
        public static List<FridaDecisionScreenshot> Take(string requestId)
        {
            lock (Sync)
            {
                List<FridaDecisionScreenshot> list;
                if (requestId == null || !ByRequest.TryGetValue(requestId, out list)) return new List<FridaDecisionScreenshot>();
                ByRequest.Remove(requestId); return list;
            }
        }
    }

    // Final decision of a team run, presented by the secretary: verdict, what was done / is pending /
    // was not done, how the plan was evaluated, evidence (tool receipts and screenshots) and the skill
    // update. Built from the structured decision and the receipts, so nothing is invented.
    public sealed class FridaTeamDecisionReport
    {
        public const string Approved = "Aprovado", ApprovedWithPending = "Aprovado com pendências", Rejected = "Reprovado", NotReviewed = "Não revisado";
        public string Request, Verdict, Justification, ExecutiveSummary, IndependenceNote, SkillUpdate, CreatedUtc;
        public int Cycles, MaxCycles;
        public List<string> Done = new List<string>(), Pending = new List<string>(), NotDone = new List<string>(), NextSteps = new List<string>();
        public List<FridaDecisionEvidence> Evidence = new List<FridaDecisionEvidence>();
        public List<FridaDecisionScreenshot> Screenshots = new List<FridaDecisionScreenshot>();
        public List<List<string>> Contributions = new List<List<string>>();

        private static string OneLine(string text, int max)
        {
            string value = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
            return value.Length > max ? value.Substring(0, max).TrimEnd() + "…" : value;
        }
        private static string Cell(string text, int max) { return OneLine(text, max).Replace("|", "/"); }
        private static string LocalTime(string utc)
        {
            DateTime at;
            return DateTime.TryParse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out at) ? at.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "";
        }
        private static void Section(StringBuilder md, string title, List<string> items, string empty)
        {
            md.AppendLine("### " + title);
            if (items.Count == 0) md.AppendLine(empty); else foreach (string item in items) md.AppendLine("- " + item);
            md.AppendLine();
        }
        // imageFolder: screenshots as Markdown images relative to an exported file; null keeps them as a list.
        public string ToMarkdown(string imageFolder = null)
        {
            var md = new StringBuilder();
            md.AppendLine("# Relatório da decisão final");
            md.AppendLine();
            md.AppendLine("- **Pedido:** " + OneLine(Request, 400));
            md.AppendLine("- **Veredito:** " + Verdict);
            md.AppendLine("- **Data:** " + LocalTime(CreatedUtc));
            md.AppendLine();
            md.AppendLine("## Resumo executivo");
            md.AppendLine(string.IsNullOrWhiteSpace(ExecutiveSummary) ? "A secretária não recebeu um resumo da equipe." : ExecutiveSummary.Trim());
            if (!string.IsNullOrWhiteSpace(Justification)) { md.AppendLine(); md.AppendLine("**Por que este veredito:** " + OneLine(Justification, 1200)); }
            md.AppendLine();
            md.AppendLine("## O que foi decidido");
            Section(md, "Feito", Done, "Nada foi confirmado como feito.");
            Section(md, "Pendente", Pending, "Nenhuma pendência registrada.");
            Section(md, "Não feito", NotDone, "Nada registrado como não feito.");
            md.AppendLine("## Como o plano foi avaliado");
            md.AppendLine("- **Ciclos de correção:** " + Cycles + " de " + MaxCycles);
            md.AppendLine("- **Revisão:** " + (string.IsNullOrWhiteSpace(IndependenceNote) ? "não informada" : IndependenceNote));
            if (Contributions.Count > 0)
            {
                md.AppendLine();
                md.AppendLine("| Papel | Modelo | Contribuição |");
                md.AppendLine("|---|---|---|");
                foreach (var c in Contributions) md.AppendLine("| " + Cell(c.Count > 0 ? c[0] : "", 60) + " | " + Cell(c.Count > 1 ? c[1] : "", 40) + " | " + Cell(c.Count > 2 ? c[2] : "", 160) + " |");
            }
            md.AppendLine();
            md.AppendLine("## Evidências");
            if (Evidence.Count == 0) md.AppendLine("Nenhum recibo de ferramenta nesta rodada: o resultado se apoia no relato da equipe.");
            else
            {
                md.AppendLine("| Ref. | Ferramenta | Situação | Resumo |");
                md.AppendLine("|---|---|---|---|");
                foreach (var e in Evidence) md.AppendLine("| " + Cell(e.Ref, 12) + " | " + Cell(e.Tool, 40) + " | " + Cell(e.Status, 30) + " | " + Cell(e.Summary, 160) + " |");
            }
            if (Screenshots.Count > 0)
            {
                md.AppendLine();
                md.AppendLine("### Prints da tela");
                foreach (var s in Screenshots)
                {
                    string name = System.IO.Path.GetFileName(s.Path), sha = (s.Sha256 ?? "").Length > 16 ? s.Sha256.Substring(0, 16) + "…" : s.Sha256;
                    if (imageFolder != null) md.AppendLine("![" + s.Caption + "](" + imageFolder + "/" + name + ")");
                    md.AppendLine("- " + s.Caption + " · " + LocalTime(s.AtUtc) + " · " + name + " · SHA-256 `" + sha + "`");
                }
            }
            md.AppendLine();
            md.AppendLine("## Skill");
            md.AppendLine(string.IsNullOrWhiteSpace(SkillUpdate) ? "Nenhuma skill foi criada ou atualizada nesta rodada." : SkillUpdate.Trim());
            if (NextSteps.Count > 0)
            {
                md.AppendLine();
                md.AppendLine("## Próximos passos");
                foreach (string step in NextSteps) md.AppendLine("- " + step);
            }
            return md.ToString();
        }
        // Chat card: verdict, counts and the first items; the summary itself is the secretary's own message.
        public string CardText()
        {
            var md = new StringBuilder("**Decisão final: " + Verdict + "**\n");
            md.Append(Done.Count + (Done.Count == 1 ? " feito" : " feitos") + " · " + Pending.Count + (Pending.Count == 1 ? " pendente" : " pendentes") + " · " + NotDone.Count + " não feito" + (NotDone.Count == 1 ? "" : "s"));
            if (Screenshots.Count > 0) md.Append(" · " + Screenshots.Count + (Screenshots.Count == 1 ? " print" : " prints"));
            md.Append("\n\n");
            if (Done.Count > 0) md.Append("- **Feito:** " + string.Join("; ", Done.Take(3).ToArray()) + (Done.Count > 3 ? "…" : "") + "\n");
            if (Pending.Count > 0) md.Append("- **Pendente:** " + string.Join("; ", Pending.Take(3).ToArray()) + (Pending.Count > 3 ? "…" : "") + "\n");
            if (NotDone.Count > 0) md.Append("- **Não feito:** " + string.Join("; ", NotDone.Take(3).ToArray()) + (NotDone.Count > 3 ? "…" : "") + "\n");
            if (!string.IsNullOrWhiteSpace(SkillUpdate)) md.Append("- **Skill:** " + OneLine(SkillUpdate.Trim().Split('\n')[0].Replace("*", "").Replace("`", ""), 160) + "\n");
            return md.ToString().TrimEnd();
        }
    }

    public static class FridaDecisionReports
    {
        private static readonly System.Text.RegularExpressions.Regex UiTools = new System.Text.RegularExpressions.Regex(@"(?i)screen|tela|browser|chrome|navegador|click|clique|mouse|keyboard|teclado|window|janela|desktop|open_app|abrir_app|^ui_", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string Normalize(string verdict)
        {
            string v = (verdict ?? "").ToLowerInvariant().Replace("ã", "a").Replace("á", "a").Replace("ê", "e").Replace("é", "e").Replace("_", " ");
            if (v.Contains("revis") && v.Contains("nao")) return FridaTeamDecisionReport.NotReviewed;
            if (v.Contains("pendenc")) return FridaTeamDecisionReport.ApprovedWithPending;
            if (v.Contains("reprov")) return FridaTeamDecisionReport.Rejected;
            if (v.Contains("aprov")) return FridaTeamDecisionReport.Approved;
            return null;
        }
        private static List<string> Items(Dictionary<string, object> spec, string key)
        {
            return FridaSkillOrchestrator.GetList(spec, key).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(20).ToList();
        }
        // Tool receipts (JSON) and research references ("[E3] ...") from the evidence the team saw.
        internal static List<FridaDecisionEvidence> ParseEvidence(string observed)
        {
            var result = new List<FridaDecisionEvidence>();
            var json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
            foreach (string raw in System.Text.RegularExpressions.Regex.Split(observed ?? "", @"\r?\n\s*\r?\n"))
            {
                string block = raw.Trim();
                if (block.Length == 0 || block.StartsWith("Nenhuma ferramenta") || block.StartsWith("Sem evidencias")) continue;
                if (block.StartsWith("{"))
                {
                    try
                    {
                        var r = json.Deserialize<Dictionary<string, object>>(block);
                        result.Add(new FridaDecisionEvidence { Ref = FridaSkillOrchestrator.GetStr(r, "reference", ""), Tool = FridaSkillOrchestrator.GetStr(r, "tool", "") + "/" + FridaSkillOrchestrator.GetStr(r, "operation", ""),
                            Status = FridaSkillOrchestrator.GetStr(r, "status", ""), Summary = FridaSkillOrchestrator.GetStr(r, "summary", "") });
                    }
                    catch (ArgumentException) { } catch (InvalidOperationException) { } // cut by the size limit
                    continue;
                }
                var reference = System.Text.RegularExpressions.Regex.Match(block, @"\[E\d+\]");
                string first = block.Split('\n')[0].Trim();
                result.Add(new FridaDecisionEvidence { Ref = reference.Success ? reference.Value.Trim('[', ']') : "", Tool = "consulta", Status = "observado", Summary = first });
            }
            return result.Take(30).ToList();
        }
        public static FridaTeamDecisionReport Build(FridaDecisionInput input)
        {
            var spec = FridaSkillOrchestrator.ExtractJson(input.Decision);
            var report = new FridaTeamDecisionReport
            {
                Request = input.Request, ExecutiveSummary = input.Summary, IndependenceNote = input.IndependenceNote, Cycles = input.Cycles, MaxCycles = input.MaxCycles,
                CreatedUtc = DateTime.UtcNow.ToString("o"), Done = Items(spec, "feito"), Pending = Items(spec, "pendente"), NotDone = Items(spec, "nao_feito"), NextSteps = Items(spec, "proximos_passos"),
                Justification = FridaSkillOrchestrator.GetStr(spec, "justificativa", null)
            };
            if (spec.Count == 0 && !string.IsNullOrWhiteSpace(input.Decision)) report.Justification = input.Decision; // free-text decision kept as the explanation
            // The verdict never claims more than the review and the receipts support.
            string verdict = Normalize(FridaSkillOrchestrator.GetStr(spec, "veredito", ""));
            if (string.IsNullOrWhiteSpace(input.Audit)) verdict = FridaTeamDecisionReport.NotReviewed;
            else if (verdict == null) verdict = input.CorrectionRequired ? FridaTeamDecisionReport.Rejected : report.Pending.Count > 0 ? FridaTeamDecisionReport.ApprovedWithPending : FridaTeamDecisionReport.Approved;
            else if (verdict == FridaTeamDecisionReport.Approved && input.CorrectionRequired)
            { verdict = FridaTeamDecisionReport.ApprovedWithPending; report.Pending.Add("A revisão apontou correções que os recibos ainda não confirmam."); }
            else if (verdict == FridaTeamDecisionReport.Approved && report.Pending.Count > 0) verdict = FridaTeamDecisionReport.ApprovedWithPending;
            report.Verdict = verdict;
            report.Evidence = ParseEvidence(input.Observed);
            report.Contributions = FridaTeamState.Transcript().Select(m => new List<string> { m.Title, m.Model ?? "", (m.Text ?? "").Split('\n').Select(l => l.Trim().TrimStart('#', ' ', '-', '*')).FirstOrDefault(l => l.Length > 0) ?? "" }).ToList();
            if (!string.IsNullOrWhiteSpace(input.SkillCreated)) report.SkillUpdate = input.SkillCreated.Replace("[NOVA SKILL INCORPORADA PELO GESTOR]", "Nova skill incorporada pelo Gestor:").Trim();
            return report;
        }
        // Lessons go into an existing skill only when the work was approved without pending items.
        private static string ApplySkillLessons(FridaTeamDecisionReport report, Dictionary<string, object> spec)
        {
            object raw;
            if (spec == null || !spec.TryGetValue("atualizar_skill", out raw)) return null;
            var update = raw as Dictionary<string, object>;
            string id = FridaSkillOrchestrator.GetStr(update, "skill_id", "");
            var lessons = FridaSkillOrchestrator.GetList(update, "licoes");
            if (string.IsNullOrWhiteSpace(id) || lessons.Count == 0) return null;
            if (report.Verdict != FridaTeamDecisionReport.Approved)
                return "A atualização da skill `" + id + "` foi proposta, mas só é aplicada quando o trabalho é aprovado sem pendências.";
            var receipt = FridaSkillRegistry.AddLessons(id, lessons);
            var skill = FridaSkillRegistry.Get(id);
            if (skill == null) return "A skill `" + id + "` citada pelo Gestor não existe no catálogo; nada foi alterado.";
            bool verified = receipt.Status == FridaResultStatus.Verified || receipt.Status == FridaResultStatus.ProcessCompleted;
            return "Skill **" + skill.Name + "** (`" + skill.Id + "`) atualizada para a versão " + skill.Version + " com " + lessons.Count + (lessons.Count == 1 ? " lição aprendida" : " lições aprendidas")
                + ". Verificação: " + (string.IsNullOrWhiteSpace(skill.VerificationCommand) ? "sem comando de verificação." : verified ? "aprovada." : "falhou; a skill segue registrada como pendente.")
                + "\n" + string.Join("\n", lessons.Select(l => "- " + l).ToArray());
        }
        public static FridaTeamDecisionReport Publish(FridaDecisionInput input)
        {
            var context = FridaIdeRuntime.Context;
            var report = Build(input);
            if (context != null)
            {
                var taken = FridaEvidenceCapture.Take(context.RequestId);
                // Work done on the screen (browser, windows, clicks) ends with a print of how it was left.
                if (taken.Count > 0 || report.Evidence.Any(e => UiTools.IsMatch(e.Tool ?? "")))
                {
                    FridaEvidenceCapture.CaptureNow("Tela ao final da execução");
                    taken.AddRange(FridaEvidenceCapture.Take(context.RequestId));
                }
                report.Screenshots = taken;
            }
            string lessons = ApplySkillLessons(report, FridaSkillOrchestrator.ExtractJson(input.Decision));
            if (lessons != null) report.SkillUpdate = string.IsNullOrWhiteSpace(report.SkillUpdate) ? lessons : report.SkillUpdate + "\n\n" + lessons;
            FridaMeetingWorkspace.Decision(report);
            return report;
        }
    }
}
