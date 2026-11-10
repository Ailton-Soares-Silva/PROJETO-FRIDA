using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    internal static class FunctionalTests
    {
        private sealed class FakeProviderHandler:HttpMessageHandler
        {
            public int Calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
            {
                Calls++;
                if(request.RequestUri.Host.Contains("groq"))return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429){Content=new StringContent("{\"error\":{\"message\":\"rate limit\"}}")});
                string decision="{\"kind\":\"conversation\",\"complexity\":\"simple\",\"requires_action\":false,\"answer\":\"Oi\"}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Json.Serialize(new{choices=new[]{new{message=new{content=decision}}}}))});
            }
        }
        private static int _passed,_failed;
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
        private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
        private static void Test(string name,Action body)
        {try{body();_passed++;Console.WriteLine("PASS "+name);}catch(Exception ex){_failed++;Console.WriteLine("FAIL "+name+": "+ex.Message);}}
        private static FridaToolReceipt Receipt(string tool,string op,string target,bool action,FridaResultStatus status)
        {
            var r=new FridaToolReceipt(tool,op,target,action);r.Check("test_observation",true,"observed","observed","fake_adapter");
            return r.Finish(status,"test","Resultado real do adaptador.");
        }
        private static void Set(object instance,string field,object value){instance.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(instance,value);}
        private static object Get(object instance,string field){return instance.GetType().GetField(field,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(instance);}
        private static string Tool(FridaBrain brain,string name,Dictionary<string,object> args)
        {return ((Task<string>)typeof(FridaBrain).GetMethod("ExecuteSingleToolAsync",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(brain,new object[]{name,args})).GetAwaiter().GetResult();}
        private static FridaSearchResult Search(string query,Dictionary<string,string[]> tree,Func<string,FileAttributes> attributes)
        {return FridaFileSearch.Find(query,new[]{"R"},TimeSpan.FromSeconds(1),CancellationToken.None,p=>tree.ContainsKey(p)?tree[p]:new string[0],attributes);}
        public static int Main(string[] options)
        {
            // Same text rendering as FridaAgent's Main (IDE layout tests measure real text widths).
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            foreach(var item in new[]{new{Text="duas horas",Seconds=7200},new{Text="meia hora",Seconds=1800},new{Text="1h30",Seconds=5400},new{Text="1 hora e 30 minutos",Seconds=5400},new{Text="1,5 horas",Seconds=5400},new{Text="uma hora e meia",Seconds=5400},new{Text="vinte e cinco minutos",Seconds=1500},new{Text="45 segundos",Seconds=45}})
            {var value=item;Test("duration "+value.Text,()=>{int seconds;Check(FridaDuration.TryParse(value.Text,out seconds)&&seconds==value.Seconds,"Duracao incorreta: "+seconds);});}
            foreach(string value in new[]{"amanha","0 segundos","-5 minutos","1000000000 horas","agende","as 18 horas"})
            {string text=value;Test("invalid duration "+text,()=>{int seconds;Check(!FridaDuration.TryParse(text,out seconds),"Duracao invalida aceita");});}
            foreach(string value in new[]{"Explique como desligar o computador","Como abrir o CMD?","Explique como listar grupos no WhatsApp","Crie um poema"})
            {string text=value;Test("explanation "+text,()=>{Check(!FridaRequestIntent.Classify(text).RequiresTools,"Acao indevida");Check(FridaNativeCommands.Execute(text,(a,b)=>{throw new Exception("Executou explicacao");})==null,"Interceptacao indevida");});}
            foreach(string value in new[]{"Quais grupos estao disponiveis no WhatsApp?","Qual e o conteudo deste arquivo?","Preciso de um programa que organize pastas","Qual CPU esta instalada?"})
            {string text=value;Test("real data "+text,()=>Check(FridaRequestIntent.Classify(text).RequiresTools,"Pedido caiu em conversa"));}
            Test("mixed explanation preserves action duration",()=>
            {
                string args=null;var r=FridaNativeCommands.Execute("Explique o desligamento em uma hora e programe para desligar em duas horas",(exe,a)=>{args=a;return Receipt("x","x","",true,FridaResultStatus.ProcessCompleted);});
                Check(args=="/s /t 7200" && r.Status==FridaResultStatus.Submitted,"Duracoes misturadas");
            });
            Test("native restart",()=>{string args=null;var r=FridaNativeCommands.Execute("Reinicie em meia hora",(e,a)=>{args=a;return Receipt("x","x","",true,FridaResultStatus.ProcessCompleted);});Check(args=="/r /t 1800"&&r.Status==FridaResultStatus.Submitted,"Operacao incorreta");});
            Test("native failure has no success",()=>{var r=FridaNativeCommands.Execute("Desligue em duas horas",(e,a)=>new FridaToolReceipt("x","x","",true).Fail("denied","Acesso negado"));Check(r.Status==FridaResultStatus.Failed && !r.Summary.Contains("aceitou"),"Sucesso falso");});
            Test("cancel without pending schedule",()=>{var r=FridaNativeCommands.Execute("Cancele o desligamento",(e,a)=>{var receipt=Receipt("x","x","",true,FridaResultStatus.Failed);receipt.Put("exit_code",1116);return receipt;});Check(r.ErrorCode=="no_shutdown_pending","Erro nao distinguido");});
            Test("missing duration never executes",()=>{var r=FridaNativeCommands.Execute("Programe o desligamento",(e,a)=>{throw new Exception("Executou sem prazo");});Check(r.ErrorCode=="missing_duration","Prazo inventado");});
            Test("negated command never dispatches",()=>{var r=FridaNativeCommands.Execute("Nao programe o desligamento em duas horas",(e,a)=>{throw new Exception("Executou comando negado");});Check(r!=null&&!r.IsAction&&r.Scope=="no_execution","Negacao ignorada");});
            Test("open prompt is different from execute command",()=>{int calls=0;Check(FridaNativeCommands.Execute("Abra o prompt de comando",(e,a)=>{calls++;return Receipt("native_terminal","open",e,true,FridaResultStatus.Submitted);})!=null,"Nao abriu prompt");Check(FridaNativeCommands.Execute("Use o CMD para executar dir",(e,a)=>{calls++;return null;})==null&&calls==1,"Executou comando como abertura");});
            Test("economy cannot downgrade action",()=>{var plan=FridaTaskRouting.Decide("Desligue em duas horas",false,false);var d=FridaEconomyRouting.Parse("{\"kind\":\"conversation\",\"complexity\":\"simple\",\"requires_action\":false,\"answer\":\"Agendei\"}",plan);FridaEconomyRouting.Apply(plan,d,"Desligue em duas horas",false);Check(!d.AnswerDirectly&&!plan.AnswersWithoutTools,"Triagem cancelou trabalho");});
            foreach(string value in new[]{"","not json","{}","{\"kind\":\"conversation\"}"})
            {string text=value;Test("invalid classifier fallback "+text,()=>{var p=FridaTaskRouting.Decide("Execute um script",false,false);var d=FridaEconomyRouting.Parse(text,p);FridaEconomyRouting.Apply(p,d,"Execute um script",false);Check(!d.AnswerDirectly,"Resposta direta indevida");});}
            Test("no tool cannot confirm model claim",()=>{var l=new FridaExecutionLedger();string response=FridaOperationalReply.Finalize(FridaRequestIntent.Classify("Abra o CMD"),l,"Pronto, abri!");Check(!response.Contains("Pronto")&&!l.TaskCompletionVerified,"Alucinacao liberada");});
            Test("query receipt cannot prove write",()=>{var l=new FridaExecutionLedger();l.Add(Receipt("manage_files","read","x",false,FridaResultStatus.Verified));string response=FridaOperationalReply.Finalize(FridaRequestIntent.Classify("Salve um arquivo"),l,"Salvei");Check(response.Contains("pendentes")&&!l.TaskCompletionVerified,"Consulta comprovou alteracao");});
            Test("partial compound request",()=>{var l=new FridaExecutionLedger();l.Add(Receipt("manage_files","write","x",true,FridaResultStatus.Verified));string response=FridaOperationalReply.Finalize(FridaRequestIntent.Classify("Crie um arquivo e abra o CMD"),l,"Tudo concluido");Check(response.Contains("pendentes")&&!l.TaskCompletionVerified,"Pedido composto concluido sem etapa");});
            Test("complete request aggregate",()=>{var l=new FridaExecutionLedger();l.Add(Receipt("manage_files","write","x",true,FridaResultStatus.Verified));FridaOperationalReply.Finalize(FridaRequestIntent.Classify("Salve um arquivo"),l,"ok");Check(l.TaskCompletionVerified && l.ToJson(false).Contains("\"task_completion_verified\":true"),"Agregado sempre falso");});
            Test("wrong target cannot complete request",()=>{var l=new FridaExecutionLedger();l.Add(Receipt("manage_files","write",@"C:\wrong.txt",true,FridaResultStatus.Verified));FridaOperationalReply.Finalize(FridaRequestIntent.Classify("Salve o arquivo \"C:\\correct.txt\""),l,"ok");Check(!l.TaskCompletionVerified,"Alvo incorreto aceito");});
            Test("stop overrides verified steps",()=>{var l=new FridaExecutionLedger();l.Add(Receipt("manage_files","write","x",true,FridaResultStatus.Verified));l.Stop("step_limit","Limite de etapas");FridaOperationalReply.Finalize(FridaRequestIntent.Classify("Salve um arquivo"),l,"Concluido");Check(!l.TaskCompletionVerified,"Limite de etapas ignorado");});
            Test("receipt belongs to only one request",()=>{var r=Receipt("x","x","x",true,FridaResultStatus.Verified);var a=new FridaExecutionLedger();a.Add(r);bool rejected=false;try{new FridaExecutionLedger().Add(r);}catch(InvalidOperationException){rejected=true;}Check(rejected,"Recibo de outro atendimento aceito");});
            Test("exact Drive name and normalization",()=>{var r=Search("Abra a pasta do meu disco G Drive PLB interno",new Dictionary<string,string[]>{{"R",new[]{@"R\PLB_INTERNO",@"R\PLB_INTERNO_OLD"}}},p=>FileAttributes.Directory);Check(r.Status=="found"&&r.Candidates.Single()==@"R\PLB_INTERNO","Alvo incorreto");});
            Test("ambiguous folders",()=>{var r=Search("PLB interno",new Dictionary<string,string[]>{{"R",new[]{@"R\PLB_INTERNO",@"R\plb-interno"}}},p=>FileAttributes.Directory);Check(r.Status=="ambiguous"&&r.Candidates.Count==2,"Ambiguidade ignorada");});
            Test("permission failure stays incomplete",()=>{var r=FridaFileSearch.Find("PLB interno",new[]{"R"},TimeSpan.FromSeconds(1),CancellationToken.None,p=>{throw new UnauthorizedAccessException();},p=>FileAttributes.Directory);Check(r.Status=="incomplete","Acesso negado virou ausencia");});
            Test("search timeout",()=>{var r=FridaFileSearch.Find("x",new[]{"R"},TimeSpan.Zero,CancellationToken.None,p=>new string[0],p=>FileAttributes.Directory);Check(r.Status=="incomplete","Timeout ignorado");});
            Test("search cancellation",()=>{var token=new CancellationToken(true);bool cancelled=false;try{FridaFileSearch.Find("x",new[]{"R"},TimeSpan.FromSeconds(1),token,p=>new string[0],p=>FileAttributes.Directory);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"Cancelamento ignorado");});
            Test("search skips links",()=>{var r=Search("target",new Dictionary<string,string[]>{{"R",new[]{@"R\link"}}},p=>FileAttributes.Directory|FileAttributes.ReparsePoint);Check(r.Status=="not_found","Link percorrido");});
            Test("search checks other siblings after denied directory",()=>{var tree=new Dictionary<string,string[]>{{"R",new[]{@"R\bad",@"R\good"}},{@"R\good",new[]{@"R\good\target"}}};var r=FridaFileSearch.Find("target",new[]{"R"},TimeSpan.FromSeconds(1),CancellationToken.None,p=>{if(p==@"R\bad")throw new UnauthorizedAccessException();return tree.ContainsKey(p)?tree[p]:new string[0];},p=>FileAttributes.Directory);Check(r.Candidates.Contains(@"R\good\target")&&r.Status=="incomplete","Uma falha abortou toda busca");});
            foreach(string status in new[]{"needs_auth","NEEDS_QR","timeout","TIMEOUT","profile_busy","dependency_missing","browser_error","dom_changed"})
            {string value=status;Test("WhatsApp status "+value,()=>{var r=FridaWhatsAppSource.Parse("list_groups","","{\"status\":\""+value+"\"}",0);Check(r.Status==FridaResultStatus.Failed,"Falha virou sucesso");if(value!="needs_auth"&&value!="NEEDS_QR")Check(r.ErrorCode!="needs_auth","Erro virou pareamento");});}
            Test("WhatsApp invalid JSON",()=>Check(FridaWhatsAppSource.Parse("status","","not json",0).ErrorCode=="invalid_reader_json","JSON invalido aceito"));
            Test("WhatsApp malformed records",()=>Check(FridaWhatsAppSource.Parse("list_groups","","{\"status\":\"success\",\"groups_filter_verified\":true,\"chats\":[1]}",0).ErrorCode=="invalid_records","Registros malformados aceitos"));
            Test("WhatsApp exit failure",()=>Check(FridaWhatsAppSource.Parse("status","","{\"status\":\"LOGGED_IN\"}",1).Status==FridaResultStatus.Failed,"Exit code ignorado"));
            Test("WhatsApp unverified conversations rejected",()=>Check(FridaWhatsAppSource.Parse("list_groups","","{\"status\":\"success\",\"chats\":[{\"name\":\"Pessoa\"}]}",0).Status==FridaResultStatus.Failed,"Contato virou grupo"));
            Test("WhatsApp empty verified list",()=>Check(FridaWhatsAppSource.Parse("list_groups","","{\"status\":\"success\",\"groups_filter_verified\":true,\"chats\":[]}",0).Status==FridaResultStatus.Verified,"Lista vazia virou QR"));
            Test("WhatsApp cache labelled",()=>{var r=FridaWhatsAppSource.Parse("search","","{\"status\":\"cached\",\"results\":[{\"text\":\"Antiga\",\"fetched_at\":\"2020-01-01\"}]}",0);string response=FridaWhatsAppSource.Reply(r,"");Check(response.Contains("cache")&&response.Contains("2020-01-01"),"Cache apresentado como atual");});
            Test("WhatsApp pairing and retry exactly once",()=>
            {
                var original=FridaWhatsAppSource.ReaderRunner;var calls=new List<string>();
                try{FridaWhatsAppSource.ReaderRunner=(op,g,q,n)=>{calls.Add(op);if(calls.Count==1)return new FridaToolReceipt("read_whatsapp",op,g,false).Fail("needs_auth","QR");if(op=="pair")return Receipt("read_whatsapp","pair","",true,FridaResultStatus.Verified);return Receipt("read_whatsapp",op,g,false,FridaResultStatus.Verified);};var r=FridaWhatsAppSource.Execute("list_groups","","",20);Check(string.Join(",",calls)=="list_groups,pair,list_groups"&&r.Status==FridaResultStatus.Verified,"Pareamento nao retomou consulta");}finally{FridaWhatsAppSource.ReaderRunner=original;}
            });
            Test("WhatsApp profile excludes concurrent request",()=>
            {
                var original=FridaWhatsAppSource.ReaderRunner;var entered=new ManualResetEventSlim();var release=new ManualResetEventSlim();Task<FridaToolReceipt> first=null;
                try{FridaWhatsAppSource.ReaderRunner=(o,g,q,n)=>{entered.Set();release.Wait(3000);return Receipt("read_whatsapp",o,g,false,FridaResultStatus.Verified);};first=Task.Run(()=>FridaWhatsAppSource.Execute("status","","",1));Check(entered.Wait(2000),"Nao iniciou");Check(FridaWhatsAppSource.Execute("status","","",1).ErrorCode=="profile_busy","Perfil usado simultaneamente");}finally{release.Set();if(first!=null)first.Wait();FridaWhatsAppSource.ReaderRunner=original;}
            });
            Test("cross-provider cache includes uncertain operation",()=>
            {
                using(var http=new HttpClient())
                {
                    var brain=new FridaBrain(new FridaSettings(),null,null,http);int calls=0;
                    Set(brain,"_requestActive",true);Set(brain,"_currentLedger",new FridaExecutionLedger());
                    Set(brain,"_readWhatsApp",new Func<string,string,string,int,FridaToolReceipt>((o,g,q,n)=>{calls++;return Receipt("read_whatsapp",o,g,true,FridaResultStatus.Unconfirmed);}));
                    var args=new Dictionary<string,object>{{"operation","status"}};string first=Tool(brain,"read_whatsapp",args),second=Tool(brain,"read_whatsapp",args);
                    Check(calls==1&&first==second,"Operacao incerta repetida");
                }
            });
            Test("heartbeat UI timeout boundary",()=>{var now=DateTime.UtcNow;Check(!FridaRuntimeHealth.UiStale(now,now.AddSeconds(-45))&&FridaRuntimeHealth.UiStale(now,now.AddSeconds(-46)),"UI travada nao detectada");});
            Test("atomic heartbeat replacement",()=>{string dir=Path.Combine(Path.GetTempPath(),"frida-health-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);try{string path=Path.Combine(dir,"health.json");FridaRuntimeHealth.AtomicWrite(path,"{\"n\":1}");FridaRuntimeHealth.AtomicWrite(path,"{\"n\":2}");Check(Convert.ToInt32(Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path))["n"])==2,"Arquivo parcial");}finally{Directory.Delete(dir,true);}});
            Test("ledger publication replaces intermediate report atomically",()=>
            {
                string dir=Path.Combine(Path.GetTempPath(),"frida-ledger-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
                try{var ledger=new FridaExecutionLedger();ledger.Save(dir);ledger.Add(Receipt("manage_files","write","x",true,FridaResultStatus.Verified));ledger.Revalidate();ledger.Save(dir);Check(Directory.GetFiles(dir,"*.json").Length==1&&ledger.PersistenceError==null&&File.ReadAllText(Directory.GetFiles(dir,"*.json")[0]).Contains("test_observation"),"Relatorio final perdido ou duplicado");}finally{Directory.Delete(dir,true);}
            });
            Test("runtime recovers interrupted request metadata",()=>{string dir=Path.Combine(Path.GetTempPath(),"frida-health-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);try{File.WriteAllText(Path.Combine(dir,"frida_heartbeat.json"),"{\"clean_shutdown\":false,\"request_id\":\"interrupted-id\",\"pending_operations\":{\"op\":\"write\"}}");FridaRuntimeHealth.Start(dir);FridaRuntimeHealth.Pulse();Check(File.ReadAllText(Path.Combine(dir,"frida_previous_interruption.json")).Contains("interrupted-id"),"Pedido interrompido perdido");FridaRuntimeHealth.Shutdown();Check(File.ReadAllText(Path.Combine(dir,"frida_heartbeat.json")).Contains("\"clean_shutdown\":true"),"Saida normal nao registrada");}finally{FridaRuntimeHealth.Shutdown();Directory.Delete(dir,true);}});
            Test("provider switch budget",()=>{var budget=new FridaProviderBudget();Check(budget.TryEnter("groq")&&budget.TryEnter("groq")&&budget.TryEnter("gemini")&&!budget.TryEnter("cerebras"),"Mais de um fallback operacional");});
            Test("HTTP 429 economy fails over using fake HTTP only",()=>
            {
                var handler=new FakeProviderHandler();using(var http=new HttpClient(handler))
                {
                    var client=new FridaTeamClient(new FridaSettings{groq_api_key="test-not-a-real-key",cerebras_api_key="test-not-a-real-key"},http);
                    string decision=client.QueryEconomyAsync("Oi").GetAwaiter().GetResult();
                    Check(handler.Calls==2&&FridaEconomyRouting.LooksLikeDecision(decision),"429 impediu fallback");
                }
            });
            Test("research preserves retrieved result without certifying conclusions",()=>
            {
                var l=new FridaExecutionLedger();l.Add(FridaVerifiedTools.Analysis("web_research","grounded_retrieval","Fonte consultada: resultado de pesquisa","provider_search"));
                string reply=FridaOperationalReply.Finalize(FridaRequestIntent.Classify("Pesquise noticias atuais"),l,"Resposta do modelo");
                Check(reply.Contains("resultado de pesquisa")&&!reply.Contains("nao foi executada")&&!l.TaskCompletionVerified,"Pesquisa real descartada ou certificada indevidamente");
            });
            Test("delete receipt does not prove save",()=>
            {
                var l=new FridaExecutionLedger();l.Add(Receipt("manage_files","delete","x",true,FridaResultStatus.Verified));
                FridaOperationalReply.Finalize(FridaRequestIntent.Classify("Salve um arquivo"),l,"ok");Check(!l.TaskCompletionVerified,"Operacao errada comprovou gravacao");
            });
            Test("10 inactivity cycles preserve wake availability",()=>
            {
                using(var http=new HttpClient())using(var voice=new AudioVoiceEngine(new FridaSettings(),null,http))
                {
                    int expired=0;voice.OnFollowUpWindowExpired+=()=>expired++;
                    for(int i=0;i<10;i++)
                    {
                        Set(voice,"_followUpWindowActive",true);Set(voice,"_offRequested",false);Set(voice,"_followUpDeadlineUtc",DateTime.UtcNow.AddSeconds(-1));
                        typeof(AudioVoiceEngine).GetMethod("CheckFollowUpTimeout",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(voice,null);
                        Check(!voice.IsOff&&!(bool)Get(voice,"_followUpWindowActive"),"Inatividade virou off");
                    }
                    Check(expired==10,"Eventos de expiracao perdidos");
                }
            });
            Test("30 native input turns keep isolated receipts and no duplicates",()=>
            {
                using(var http=new HttpClient())
                {
                    var brain=new FridaBrain(new FridaSettings(),null,null,http);int calls=0;var ids=new HashSet<string>();
                    brain.NativeRunner=(e,a)=>{calls++;return Receipt("native_power","schedule","Windows",true,FridaResultStatus.ProcessCompleted);};
                    for(int i=0;i<30;i++)
                    {
                        string reply=brain.ProcessUserRequestAsync("Desligue em duas horas",null).GetAwaiter().GetResult();
                        var report=Json.Deserialize<Dictionary<string,object>>(brain.LastExecutionReportJson);ids.Add(Convert.ToString(report["request_id"]));
                        Check(reply.Contains("aceitou")&&!Convert.ToBoolean(report["task_completion_verified"]),"Resultado incorreto ou aceito confundido com verificado");
                    }
                    Check(calls==30&&ids.Count==30&&!brain.IsRequestRunning,"Atendimentos duplicados ou travados");
                }
            });
            Test("native input updates text speech and report from real result",()=>
            {
                using(var http=new HttpClient())
                {
                    var brain=new FridaBrain(new FridaSettings(),null,null,http);int calls=0;
                    brain.NativeRunner=(e,a)=>{calls++;return new FridaToolReceipt("native_power","schedule","Windows",true).Fail("test_failure","Falha simulada do Windows.");};
                    string reply=brain.ProcessUserRequestAsync("Desligue em duas horas",null).GetAwaiter().GetResult();
                    Check(calls==1&&reply.Contains("Falha simulada")&&!brain.LastSpokenResponse.Contains("agendei")&&brain.LastExecutionReportJson.Contains("test_failure"),"Resposta, fala e recibo divergiram");
                }
            });
            Test("missing duration is completed by the next answer",()=>
            {
                using(var http=new HttpClient())
                {
                    var brain=new FridaBrain(new FridaSettings(),null,null,http);int calls=0;string arguments=null;
                    brain.NativeRunner=(e,a)=>{calls++;arguments=a;return Receipt("native_power","schedule","Windows",true,FridaResultStatus.ProcessCompleted);};
                    string question=brain.ProcessUserRequestAsync("Programe o desligamento",null).GetAwaiter().GetResult();
                    Check(calls==0&&question.Contains("quanto tempo"),"Pergunta de prazo ausente");
                    string reply=brain.ProcessUserRequestAsync("duas horas",null).GetAwaiter().GetResult();
                    Check(calls==1&&arguments=="/s /t 7200"&&reply.Contains("aceitou"),"Resposta ao esclarecimento nao retomou a operacao");
                }
            });
            Test("ambiguous folder choice resolves the exact pending path",()=>
            {
                using(var http=new HttpClient())
                {
                    var brain=new FridaBrain(new FridaSettings(),null,null,http);
                    Set(brain,"_pendingOpenCandidates",new[]{@"C:\first",@"G:\Meu Drive\PLB_INTERNO"});Set(brain,"_pendingOpenUtc",DateTime.UtcNow);
                    string resolved=(string)typeof(FridaBrain).GetMethod("ResolvePendingNativeRequest",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(brain,new object[]{"segunda"});
                    Check(resolved=="Abra \"G:\\Meu Drive\\PLB_INTERNO\""&&Get(brain,"_pendingOpenCandidates")==null,"Escolha nao foi ligada ao caminho pendente");
                }
            });
            Test("browser and screen requests route to full tools and block capability refusal",()=>
            {
                foreach(string actual in new[]{"Pesquise no Google Chrome Filamento PP da Winkel e coloque os resultados em minha tela.","Pesquise no Google Chrome filamento PP", "pesquisa no meu Google Chrome Filamento PP. Coloca o resultado na minha tela."})
                {
                    var actualPlan=FridaTaskRouting.Decide(actual,false,false);
                    var downgrade=FridaEconomyRouting.Parse("{\"kind\":\"research\",\"complexity\":\"simple\",\"requires_action\":true,\"answer\":\"\"}",actualPlan);
                    FridaEconomyRouting.Apply(actualPlan,downgrade,actual,false);
                    Check(actualPlan.Kind==FridaTaskKind.Technical && !actualPlan.ResearchOnly && !actualPlan.EconomyMode && !downgrade.AnswerDirectly,"Triagem eliminou acao de navegador: "+actual);
                }
                string navReq="Agora meu navegador está aberto em minha tela, acesse meu navegador e coloque esse filamento que você achou na minha tela.";
                var intent=FridaRequestIntent.Classify(navReq);
                Check(intent.RequiresTools && intent.Operations.Count==1 && intent.Operations[0].Kind=="open","Pedido de navegador/tela nao exigiu open_program_or_url");
                var plan=FridaTaskRouting.Decide(navReq,true,false);
                Check(plan.Kind==FridaTaskKind.Technical,"Pedido de navegador/tela nao foi para Technical");
                var refusal=FridaEconomyRouting.Parse("{\"kind\":\"conversation\",\"complexity\":\"simple\",\"requires_action\":false,\"answer\":\"Não posso interagir diretamente com a sua tela ou controlar o navegador aberto.\"}",plan);
                FridaEconomyRouting.Apply(plan,refusal,navReq,false);
                Check(!refusal.AnswerDirectly && string.IsNullOrEmpty(refusal.Answer) && !plan.AnswersWithoutTools && plan.Kind==FridaTaskKind.Technical,"Recusa de capacidade passou pela triagem");

                string chromeSearch="Chrome e pesquisa sobre PP WinCode preto.";
                Check(FridaRequestIntent.Classify(chromeSearch).RequiresTools && FridaTaskRouting.Decide(chromeSearch,false,false).Kind==FridaTaskKind.Technical,"Abertura composta do Chrome com pesquisa ignorada");

                string screenReq="Veja o que está na minha tela";
                var screenIntent=FridaRequestIntent.Classify(screenReq);
                Check(screenIntent.RequiresTools && screenIntent.Operations.Single().Kind=="screen","Inspecao de tela nao classificada");
                var ledger=new FridaExecutionLedger();
                ledger.Add(FridaVerifiedTools.Analysis("inspect_screen","visual_interpretation","Navegador aberto exibindo filamento PP.","screen_snapshot"));
                string screenReply=FridaOperationalReply.Finalize(screenIntent,ledger,"");
                Check(screenReply.Contains("Navegador aberto") && !screenReply.Contains("nao foi executada"),"Recibo de inspect_screen descartado");
            });
            Test("store stock and brand follow-up route to web research and preserve research kind",()=>
            {
                string stockReq="Esse PPI que você achou tem unidades disponíveis no estoque, na Procreto?";
                Check(FridaEconomyRouting.NeedsFreshData(stockReq),"Consulta de estoque nao exigiu dados atualizados");
                var stockPlan=FridaTaskRouting.Decide(stockReq,true,false);
                var stockRefusal=FridaEconomyRouting.Parse("{\"kind\":\"conversation\",\"complexity\":\"simple\",\"requires_action\":false,\"answer\":\"Não tenho acesso ao estoque da Procreto.\"}",stockPlan);
                FridaEconomyRouting.Apply(stockPlan,stockRefusal,stockReq,false);
                Check(!stockRefusal.AnswerDirectly && stockPlan.Kind==FridaTaskKind.Research,"Consulta de estoque nao foi para web_research");

                string brandReq="e a marca WinPlay.";
                Check(FridaEconomyRouting.NeedsFreshData(brandReq),"Follow-up de marca nao exigiu pesquisa");
                var history=new List<KeyValuePair<string,string>>{new KeyValuePair<string,string>("user","pesquise filamentos PP"),new KeyValuePair<string,string>("model","Filamento PP 1,75mm")};
                Check(FridaInteractionPolicy.WithConversationReference(brandReq,history).Contains("[REFERENCIA DA CONVERSA]"),"Follow-up de marca perdeu contexto");

                string pureRes="pesquise filamentos PP da Winkley.";
                var resPlan=FridaTaskRouting.Decide(pureRes,false,false);
                var resDec=FridaEconomyRouting.Parse("{\"kind\":\"research\",\"complexity\":\"simple\",\"requires_action\":true,\"answer\":\"\"}",resPlan);
                FridaEconomyRouting.Apply(resPlan,resDec,pureRes,false);
                Check(resPlan.Kind==FridaTaskKind.Research,"Pesquisa pura foi sobrescrita para Technical");

                var resLedger=new FridaExecutionLedger();
                string longTable=new string('A',450);
                resLedger.Add(FridaVerifiedTools.Analysis("web_research","grounded_retrieval",longTable,"provider_search"));
                string dedup=FridaOperationalReply.Finalize(FridaRequestIntent.Classify(pureRes),resLedger,new string('B',420));
                Check(dedup.Contains(longTable) && !dedup.Contains(new string('B',420)),"Tabela longa de pesquisa foi duplicada na resposta final");
            });
            Test("casual narrative rodar comment stays conversation without false tool failure",()=>
            {
                string casual="pra poder rodar com o GAC. E aí anunciado que não vai sair pra terceiro, vai sair só pra os videogames rodando a 30 fps. E eu com a cara de pateta muito.";
                Check(!FridaInteractionPolicy.ExplicitAction(casual),"Comentario casual marcado como acao explicita");
                Check(!FridaRequestIntent.Classify(casual).RequiresTools,"Comentario casual exigiu ferramentas");
                Check(FridaTaskRouting.Decide(casual,false,false).Kind==FridaTaskKind.Conversation,"Comentario casual saiu de Conversation");
            });
            foreach(string value in new[]{"Me ensine inglês","Quero aprender Python","Explique o aprendizado de máquina","Descubra como está o tempo em São Paulo","Pesquise formas de reduzir custos de frete","Vou aprender a dirigir amanhã"})
            {string text=value;Test("operator learning is not skill evolution: "+text,()=>Check(FridaTaskRouting.Decide(text,false,false).Kind!=FridaTaskKind.SkillEvolution,"Pedido comum virou evolucao de skill"));}
            foreach(string value in new[]{"Crie uma skill para consolidar os relatórios","Aprenda a gerar o relatório de placas pendentes","Frida, quero que você aprenda como exportar o PDF do Kanban","Vou te ensinar a conferir o estoque","Atualize suas skills com o POP novo","Adicione essa habilidade ao seu catálogo"})
            {string text=value;Test("explicit skill request is skill evolution: "+text,()=>{var p=FridaTaskRouting.Decide(text,false,false);Check(p.Kind==FridaTaskKind.SkillEvolution && p.NeedsTeam,"Pedido de skill nao reconhecido: "+p.Kind);});}
            Test("triage model routes a paraphrased skill request without keywords",()=>
            {
                string request="Guarde esse procedimento para repetir sozinha da próxima vez";
                var plan=FridaTaskRouting.Decide(request,false,false);
                Check(plan.Kind!=FridaTaskKind.SkillEvolution,"Regex nao deveria reconhecer esta redacao");
                var d=FridaEconomyRouting.Parse("{\"kind\":\"skill_evolution\",\"complexity\":\"moderate\",\"requires_action\":true,\"answer\":\"\"}",plan);
                FridaEconomyRouting.Apply(plan,d,request,false);
                Check(d.Valid && plan.Kind==FridaTaskKind.SkillEvolution && plan.NeedsTeam && !d.AnswerDirectly,"Triagem nao levou a evolucao de skill: "+plan.Kind);
                // The operator wanting to learn is never upgraded by the triage answer alone.
                var chat=FridaTaskRouting.Decide("Me ensine inglês",false,false);
                var c=FridaEconomyRouting.Parse("{\"kind\":\"conversation\",\"complexity\":\"simple\",\"requires_action\":false,\"answer\":\"Claro, vamos começar.\"}",chat);
                FridaEconomyRouting.Apply(chat,c,"Me ensine inglês",false);
                Check(chat.Kind!=FridaTaskKind.SkillEvolution,"Conversa virou evolucao de skill");
            });
            Test("skill complexity follows the manager and ignores the preposition com",()=>
            {
                Check(FridaSkillOrchestrator.ClassifyComplexity("Gere um relatório com os dados do mês",null)==FridaSkillComplexity.Low,"Preposicao 'com' elevou a complexidade");
                Check(FridaSkillOrchestrator.ClassifyComplexity("Gere um relatório com os dados do mês","High")==FridaSkillComplexity.High,"Avaliacao do gestor ignorada");
                Check(FridaSkillOrchestrator.ClassifyComplexity("Automação COM do SolidWorks para exportar desenhos","Low")==FridaSkillComplexity.Low,"Avaliacao do gestor ignorada (Low)");
                Check(FridaSkillOrchestrator.ClassifyComplexity("Automação COM do SolidWorks para exportar desenhos","")==FridaSkillComplexity.High,"Automacao COM nao reconhecida");
                Check(FridaSkillOrchestrator.ClassifyComplexity("Converta o CSV em JSON","")==FridaSkillComplexity.Medium,"Conversao de formato nao reconhecida");
            });
            Test("trivial verification commands are recognised",()=>
            {
                foreach(string trivial in new[]{"","Write-Output 'OK'","write-host ok","echo ok","exit 0","$true","'OK'"})
                    Check(FridaSkillRegistry.IsTrivialVerification(trivial),"Comando trivial aceito: "+trivial);
                foreach(string real in new[]{"python run.py --check","& .\\run.ps1 -Check","python run.py --check; Write-Output 'OK'","pytest -q"})
                    Check(!FridaSkillRegistry.IsTrivialVerification(real),"Comando real recusado: "+real);
            });
            if(options.Contains("--drive-smoke"))
            {
                foreach(string folder in new[]{"PLB_INTERNO","JSA_INTERNO"})
                {string name=folder;Test("LIVE Drive "+name,()=>{string path=FridaFileSearch.Resolve("Abra a pasta do disco G Drive "+name.Replace('_',' '));Check(path==@"G:\Meu Drive\"+name && Directory.Exists(path),"Alvo real incorreto: "+path);});}
            }
            if(options.Contains("--windows-smoke"))
            {
                Test("LIVE Windows schedules and cancels in cleanup",()=>
                {
                    FridaToolReceipt scheduled=null,cancelled=null;
                    try
                    {
                        scheduled=FridaNativeCommands.Execute("Programe o computador para desligar em duas horas",FridaNativeCommands.Run);
                        Console.WriteLine("LIVE_SCHEDULE "+scheduled.ToJson());
                        Check(scheduled.Status==FridaResultStatus.Submitted,"Windows nao aceitou o agendamento");
                    }
                    finally
                    {
                        // Do not cancel a pre-existing schedule if our request was rejected.
                        if(scheduled!=null && scheduled.Status==FridaResultStatus.Submitted)
                        {
                            cancelled=FridaNativeCommands.Execute("Cancele o desligamento do computador",FridaNativeCommands.Run);
                            Console.WriteLine("LIVE_CANCEL "+cancelled.ToJson());
                            Check(cancelled.Status==FridaResultStatus.Submitted,"Windows nao confirmou o cancelamento");
                        }
                    }
                });
            }
            KeywordFreeTests.Run(Test);
            WhatsAppTests.Run(Test);
            CodingCopilotTests.Run(Test);
            IdeTests.Run(Test);
            VoiceRecognitionTests.Run(Test);
            Console.WriteLine("RESULT passed="+_passed+" failed="+_failed);return _failed==0?0:1;
        }
    }
}
