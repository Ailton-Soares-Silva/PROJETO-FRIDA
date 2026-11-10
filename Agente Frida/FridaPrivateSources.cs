using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public sealed class FridaWhatsAppRequest
    {
        public string Operation="search",GroupName="",Query="";
        internal FridaWhatsAppPlan DirectPlan;
        internal bool InheritChat;
        internal static bool SessionStatus(string text)
        {
            return Regex.IsMatch(text, @"^(?:(?:qual|como|verifique|verificar|veja|cheque|consulte|mostrar|o|a|esta|se|me mostre) )*(?:status|conexao|conectado|pareado)(?: (?:do|da|no|a|o|de|leitor|sessao|conexao|pareamento|whatsapp|web|zap|wpp|whats|esta|ativo|conectado|pareado|agora))*$")
                || Regex.IsMatch(text, @"^(?:(?:verifique se|o|a|meu|minha|leitor|sessao|do|da) )*(?:whatsapp|zap|whats|wpp)(?: web)? (?:esta |ta |continua )?(?:conectado|pareado|online)$");
        }
        public static FridaWhatsAppRequest Detect(string request,IEnumerable<KeyValuePair<string,string>> history)
        {
            string t=FridaInteractionPolicy.Normalize(request);
            if(Regex.IsMatch(t,@"\b(na internet|na web|no google|noticias publicas)\b"))return null;
            bool teamChat=Regex.IsMatch(t,@"\b(falou sobre|mandou sobre|ultimo recado|recado da equipe|recado do|nas mensagens|no grupo|nos grupos|relatou hoje|confirmou o recebimento|pediu alteracao|pessoal do corte|time da montagem|serralheiro mandou)\b");
            if(!teamChat && Regex.IsMatch(t,@"\b(planilha|planilhas|repositorio|repositório|drive|google drive|cruze|cruzada|cruzado|cruzar|chassi|chassis|implemento|implementos|placa|placas|gestor|cerebro|cérebro|estagiario|estagiário|analista|senior|sênior|engenheiro|equipe|skill|skills|habilidade|habilidades|aprenda|aprender|instale|instalar)\b"))return null;
            if(Regex.IsMatch(t,@"^(?:(?:me )?explique\b|como (?:usar|listar|pesquisar|buscar)\b|nao (?:busque|pesquise|procure|leia)\b)"))return null;
            if(Regex.IsMatch(t,@"^(?:me explique |explique )?(?:como funciona|como funcionam|o que e o whatsapp|como usar)\b")&&!Regex.IsMatch(t,@"\b(leia|pesquise|busque|consulte)\b"))return null;
            if(Regex.IsMatch(t,@"^(?:por favor )?(?:abra|abrir|abre|inicie)\b"))return null;
            bool named=Regex.IsMatch(t,@"\b(whatsapp|whats|wpp|zap)\b");
            bool implicitChat=teamChat
                || Regex.IsMatch(t,@"\b(no grupo|do grupo|nos grupos|na conversa com|da conversa com|conversa do grupo|grupo importar|falaram no grupo|mandaram no grupo)\b")
                || Regex.IsMatch(t,@"\b(alguem me mandou|me mandaram|mandou uma mensagem|mandaram mensagem|tem mensagem nova|mensagens nao lidas|quem me mandou mensagem|recebi alguma mensagem)\b")
                || Regex.IsMatch(t,@"\b(cada mensagem que voce leu|todas as mensagens do grupo|nesse grupo que voce leu)\b");
            var historyList=(history??Enumerable.Empty<KeyValuePair<string,string>>()).ToArray();
            var users=historyList.Where(x=>x.Key=="user").Reverse().Take(4);
            var models=historyList.Where(x=>x.Key=="model").Reverse().Take(2);
            bool context=users.Any(x=>
            {
                string u=FridaInteractionPolicy.Normalize(x.Value);
                return Regex.IsMatch(u,@"\b(whatsapp|whats|wpp|zap)\b")||Regex.IsMatch(u,@"\b(no grupo|do grupo|nos grupos|na conversa com|alguem me mandou)\b");
            }) || models.Any(x=>Regex.IsMatch(x.Value??"",@"\[M[a-f0-9]{16}\]|Cobertura:\s*(?:accessible_web_search|loaded_web_history|cache_only)"));
            bool isLocalSystemCommand=Regex.IsMatch(t,@"\b(powershell|cmd|terminal|compil[ae]|codigo|script|python|csharp|solidworks|autocad|excel|word|navegador|chrome|edge|deslig[au]|reinici[ae]|volume|mudo|print|captur[ae]\s+a\s+tela)\b");
            bool follow=!isLocalSystemCommand && (
                Regex.IsMatch(t,@"^(?:e )?(?:ontem|hoje|anteontem|continue|continuar|mais|ness[ea]|ness[ea]s|nos contatos|nos grupos|na conversa|no grupo|segunda|primeira|[1-9])\b")
                || Regex.IsMatch(t,@"^(?:e\s+)?(?:alem\s+des[st]e|alem\s+diss[oo]|existe\s+mais|tem\s+mais|ha\s+mais|existe\s+outro|tem\s+outro|mais\s+algum|algum\s+outro|separe|classifique|some|somar|compare|detalhe|liste)\b")
                || Regex.IsMatch(t,@"\b(grupos?|mensagens?|conversa|historico|registros?|pecas\s+entregues|pecas\s+produzidas|dobradeiras?|guilhotinas?|varredeiras?|que\s+voce\s+leu|que\s+encontramos|outro\s+registro|mais\s+algum\s+outro)\b")
                || FridaWhatsAppPlan.ParseChoice(request)>0);
            var lastUser=historyList.LastOrDefault(x=>x.Key=="user").Value;
            string recent=FridaInteractionPolicy.Normalize(lastUser);
            bool recentWhatsApp=Regex.IsMatch(recent,@"\b(whatsapp|whats|wpp|zap|nos grupos|no grupo)\b");
            string subject=request;
            if(context && Regex.IsMatch(t,@"^(ok pode fazer|pode fazer|pode sim|sim|ok)$") &&
                Regex.IsMatch(FridaInteractionPolicy.Normalize(historyList.LastOrDefault(x=>x.Key=="model").Value),
                    @"\b(posso|basta solicitar|vou (?:abrir|executar|consultar))\b") &&
                FridaWhatsAppPlan.SimpleStatus(lastUser,false)!=null)
                subject=lastUser;
            var simple = FridaWhatsAppPlan.SimpleStatus(subject,context && users.Any(x=>Regex.IsMatch(FridaInteractionPolicy.Normalize(x.Value),@"\bgrupos\b")));
            if(!named&&!implicitChat&&!(context&&follow)&&!(simple!=null&&(recentWhatsApp||subject!=request)))return null;
            if(Regex.IsMatch(t,@"\b(pareie|parear|conecte|conectar|vincule|vincular)\b"))return new FridaWhatsAppRequest{Operation="pair"};
            if(SessionStatus(t))return new FridaWhatsAppRequest{Operation="status"};
            return new FridaWhatsAppRequest { DirectPlan=simple, InheritChat=simple!=null && !Regex.IsMatch(FridaInteractionPolicy.Normalize(subject),@"\b(grupos|whatsapp|whats|zap|wpp)\b") };
        }
    }
    public static class FridaWhatsAppSource
    {
        private static readonly HashSet<string> ChatStopwords=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "de","da","do","das","dos","e","o","a","os","as","no","na","nos","nas","em","um","uma","grupo","conversa","chat","contato","whatsapp","zap","wpp"
        };
        public static bool SmartChatNameMatch(string actualChat,string expectedChat)
        {
            string actual=FridaInteractionPolicy.Normalize(actualChat);
            string expected=FridaInteractionPolicy.Normalize(expectedChat);
            if(string.IsNullOrWhiteSpace(expected))return true;
            if(string.IsNullOrWhiteSpace(actual))return false;
            if(actual.IndexOf(expected,StringComparison.Ordinal)>=0)return true;
            var expectedTokens=expected.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries).Where(t=>!ChatStopwords.Contains(t)).ToArray();
            if(expectedTokens.Length==0)expectedTokens=expected.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
            var actualTokens=actual.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
            if(expectedTokens.Length==0)return false;
            int matched=expectedTokens.Count(et=>actualTokens.Any(at=>at==et||at.StartsWith(et,StringComparison.Ordinal)));
            return matched==expectedTokens.Length||(expectedTokens.Length>=3&&matched>=2&&matched>=expectedTokens.Length-1);
        }
        private static string Quote(string value)
        {
            var text=new StringBuilder("\"");int slashes=0;
            foreach(char c in value ?? "")
            {if(c=='\\'){slashes++;continue;}text.Append('\\',c=='\"'?slashes*2+1:slashes);text.Append(c);slashes=0;}
            text.Append('\\',slashes*2);return text.Append('\"').ToString();
        }
        private static readonly SemaphoreSlim ProfileGate=new SemaphoreSlim(1,1);
        private static FridaSettings _settings=new FridaSettings();
        public static Action<string> Progress;
        public static Action<string> VisualProgress;
        internal static Func<string,string,string,int,FridaToolReceipt> ReaderRunner=RunReader;
        private static FridaWhatsAppWorker _worker,_cacheWorker;
        // Test seam for the v2 protocol; null in production (owned Python worker).
        internal static Func<Dictionary<string,object>,Dictionary<string,object>> WorkerRunner=null;
        public static void Configure(FridaSettings settings){Shutdown();_settings=settings ?? new FridaSettings();}
        public static void Shutdown(){if(_worker!=null)_worker.Dispose();if(_cacheWorker!=null)_cacheWorker.Dispose();_worker=null;_cacheWorker=null;}
        internal static event Action<string> QrChanged;
        internal static void PublishQr(string png)
        {
            var handler = QrChanged;
            if (handler != null) handler(png);
        }
        internal static FridaToolReceipt ObserveConnection(bool pair)
        {
            if (!ProfileGate.Wait(0)) return new FridaToolReceipt("read_whatsapp", "status", "", false).Fail("profile_busy", "Leitor ocupado. Aguarde a consulta terminar.");
            try
            {
                using (FridaIdeRuntime.Enter(new FridaIdeContext { Silent = true }))
                    return ExecuteRaw(new Dictionary<string,object> {{"operation",pair ? "pair" : "status"},{"embedded",true}});
            }
            finally { ProfileGate.Release(); }
        }
        internal static FridaToolReceipt ExecuteRaw(Dictionary<string,object> args)
        {
            bool cache=FridaWhatsAppWorker.Value(args,"operation")=="search_cache";
            Dictionary<string,object> result;
            if(WorkerRunner!=null)result=WorkerRunner(args);
            else
            {
                FridaWhatsAppWorker worker;
                lock(_settings){if(cache){if(_cacheWorker==null)_cacheWorker=new FridaWhatsAppWorker(_settings);worker=_cacheWorker;}else{if(_worker==null)_worker=new FridaWhatsAppWorker(_settings);worker=_worker;}}
                Action<string> progressHandler = FridaIdeRuntime.Silent
                    ? (Action<string>)(message =>
                    {
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            FridaIdeRuntime.Publish("WhatsApp · " + message);
                            var visual = VisualProgress;
                            if (visual != null) visual(message);
                        }
                    })
                    : Progress;
                result=worker.Execute(args,progressHandler);
            }
            return Parse(FridaWhatsAppWorker.Value(args,"operation"),FridaWhatsAppWorker.Value(args,"chat_name"),new JavaScriptSerializer{MaxJsonLength=32*1024*1024}.Serialize(result),0);
        }
        public static FridaToolReceipt ExecuteRequest(Dictionary<string,object> args)
        {
            string operation=FridaWhatsAppWorker.Value(args,"operation");
            if(operation=="search_cache")return ExecuteRaw(args);
            ProfileGate.Wait(FridaWorkContext.Token);
            try
            {
                var result=ExecuteRaw(args);
                if(result.ErrorCode=="needs_auth"&&operation!="pair")
                {
                    if (FridaIdeRuntime.Silent) FridaIdeRuntime.RequestPairing();
                    else if(Progress!=null)Progress("O WhatsApp precisa de conexao. Leia o QR Code na janela do leitor.");
                    var paired=ExecuteRaw(new Dictionary<string,object>{{"operation","pair"},{"embedded",FridaIdeRuntime.Silent}});
                    if (FridaIdeRuntime.Silent) FridaIdeRuntime.FinishPairing(paired.Summary);
                    if(paired.Status!=FridaResultStatus.Verified)return paired;
                    FridaWorkContext.ThrowIfCancelled();result=ExecuteRaw(args);
                }
                return result;
            }
            finally{ProfileGate.Release();}
        }
        public static FridaToolReceipt Execute(string operation,string group,string query,int limit)
        {
            if(!ProfileGate.Wait(0))return new FridaToolReceipt("read_whatsapp",operation,group,false).Fail("profile_busy","A conexao do WhatsApp esta em uso ou em pareamento. Aguarde a operacao atual.");
            try
            {
                var result=ReaderRunner(operation,group,query,limit);
                if(result.ErrorCode=="needs_auth" && operation!="pair")
                {
                    if(Progress!=null)Progress("O WhatsApp precisa de conexao. Abrindo o QR Code; aguardando voce escanear pelo celular.");
                    var paired=ReaderRunner("pair",null,null,1);
                    if(paired.Status!=FridaResultStatus.Verified)return paired;
                    FridaWorkContext.ThrowIfCancelled();
                    result=ReaderRunner(operation,group,query,limit);
                }
                return result;
            }
            finally{ProfileGate.Release();}
        }
        private static string PythonExecutable()
        {
            string configured=string.IsNullOrWhiteSpace(_settings.whatsapp_python)?Environment.GetEnvironmentVariable("FRIDA_PYTHON"):_settings.whatsapp_python;
            if(!string.IsNullOrWhiteSpace(configured))return Environment.ExpandEnvironmentVariables(configured);
            string local=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Python");
            if(Directory.Exists(local))foreach(string dir in Directory.GetDirectories(local).OrderByDescending(x=>x))
            {string exe=Path.Combine(dir,"python.exe");if(File.Exists(exe))return exe;}
            foreach(string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {try{string exe=Path.Combine(dir.Trim('"'),"python.exe");if(File.Exists(exe))return exe;}catch(ArgumentException){}}
            return null;
        }
        private static FridaToolReceipt RunReader(string operation,string group,string query,int limit)
        {
            return ExecuteRaw(new Dictionary<string,object>{{"operation",operation},{"chat_name",group??""},{"query",query??""},{"limit",limit}});
        }
        public static FridaToolReceipt Parse(string operation,string group,string output,int exitCode)
        {
            var receipt=new FridaToolReceipt("read_whatsapp",operation,group,operation=="pair");
            if(exitCode!=0)return receipt.Fail("reader_process_error","O processo de leitura do WhatsApp terminou com erro; nenhuma mensagem foi confirmada.");
            Dictionary<string,object> root;
            try{root=new JavaScriptSerializer{MaxJsonLength=8*1024*1024}.Deserialize<Dictionary<string,object>>(output);}catch{return receipt.Fail("invalid_reader_json","O leitor nao retornou um registro JSON valido.");}
            object schema;if(root!=null&&root.TryGetValue("schema_version",out schema)&&Convert.ToInt32(schema)==2)return ParseV2(operation,group,root);
            object raw;string status=root!=null && root.TryGetValue("status",out raw)?Convert.ToString(raw):"";
            if(status=="needs_auth" || status=="NEEDS_QR")return receipt.Fail("needs_auth","A sessao de leitura do WhatsApp da Frida nao esta conectada. Ter o WhatsApp aberto em outra janela nao confirma essa conexao. Conecte a sessao do leitor antes de consultar os grupos.");
            if(status=="timeout" || status=="TIMEOUT")return receipt.Fail("reader_timeout","O WhatsApp nao respondeu no prazo. A conexao ou leitura nao foi confirmada.");
            if(status=="profile_busy" || status=="dependency_missing" || status=="browser_error" || status=="dom_changed")
                return receipt.Fail(status,root.ContainsKey("message")?Convert.ToString(root["message"]):"O leitor falhou: "+status);
            if(operation=="pair")
            {
                if(status!="success" || !root.TryGetValue("authenticated",out raw) || !(raw is bool) || !(bool)raw)return receipt.Fail("pair_unconfirmed","O pareamento nao foi confirmado.");
                receipt.Check("authenticated",true,"true","true","browser_authenticated_pane");
                return receipt.Finish(FridaResultStatus.Verified,"authenticated_profile","WhatsApp pareado e sessao salva.",output);
            }
            if(status=="not_found" || status=="ambiguous_group")return receipt.Fail(status,status=="not_found"?"O grupo solicitado nao foi encontrado pelo leitor. Confira o nome; nenhuma mensagem desse grupo foi confirmada.":"Mais de um grupo corresponde ao nome. Informe o nome exato antes da leitura.");
            if(status=="group_mismatch")return receipt.Fail(status,"O leitor nao confirmou que a conversa aberta corresponde ao grupo solicitado. Nenhuma mensagem desse grupo foi confirmada.");
            if(status!="success" && status!="cached" && !(operation=="status" && status=="LOGGED_IN"))return receipt.Fail("reader_unavailable","O leitor do WhatsApp nao confirmou uma leitura. Estado recebido: "+status);
            string source=status=="cached" || operation=="search"?"local_cache_snapshot":"browser_dom_snapshot";
            string key=operation=="list_groups"?"chats":operation=="read_unread"?"unread_chats":operation=="search"?"results":"messages";
            var items=root.TryGetValue(key,out raw)?raw as IEnumerable:null;
            if(operation!="status" && (items==null || raw is string || raw is Dictionary<string,object>))return receipt.Fail("missing_records","O leitor confirmou estado, mas nao forneceu os registros solicitados.");
            if(operation!="status" && items.Cast<object>().Any(item=>!(item is Dictionary<string,object>)))return receipt.Fail("invalid_records","O leitor retornou registros malformados.");
            if(operation=="read_group" && status=="success" && (!root.TryGetValue("group_header_verified",out raw) || !(raw is bool) || !(bool)raw))
                return receipt.Fail("group_not_verified","O leitor retornou mensagens sem confirmar o cabecalho do grupo. A origem nao foi verificada.");
            if(operation=="read_group" && status=="success" && (!root.TryGetValue("group",out raw) || string.IsNullOrWhiteSpace(Convert.ToString(raw)) || (!string.IsNullOrWhiteSpace(group) && !SmartChatNameMatch(Convert.ToString(raw),group))))
                return receipt.Fail("group_target_mismatch","O grupo retornado nao corresponde ao nome solicitado. Nenhuma mensagem desse grupo foi confirmada.");
            if(operation=="list_groups" || operation=="read_unread" || operation=="read_group" && status=="success")
            {
                if(!root.TryGetValue("groups_filter_verified",out raw) || !(raw is bool) || !(bool)raw)return receipt.Fail("groups_not_verified","A interface nao confirmou que os registros pertencem a grupos.");
                if(operation!="read_group" && items.Cast<object>().OfType<Dictionary<string,object>>().Any(item=>!item.ContainsKey("is_group") || !(item["is_group"] is bool) || !(bool)item["is_group"]))return receipt.Fail("groups_not_verified","A lista contem conversas sem confirmacao de grupo.");
            }
            receipt.Check("reader_exit_code",true,"0",exitCode.ToString(),"owned_process");receipt.Check("reader_json_status",true,"success, cached or LOGGED_IN",status,"local_json_parser");
            receipt.Put("source",source);receipt.Put("retrieved_at_utc",DateTime.UtcNow.ToString("o"));receipt.Put("reader_result",root);
            return receipt.Finish(FridaResultStatus.Verified,source,"Leitura pontual do WhatsApp recebida do leitor local; a interpretacao e a completude do historico nao foram certificadas.",output);
        }
        private static FridaToolReceipt ParseV2(string operation,string group,Dictionary<string,object> root)
        {
            var receipt=new FridaToolReceipt("read_whatsapp",operation,group,operation=="pair");
            string status=FridaWhatsAppWorker.Value(root,"status");
            receipt.Put("reader_result",root);receipt.Put("source",FridaWhatsAppWorker.Value(root,"source"));
            if(status!="success"&&status!="partial"&&status!="cached"&&status!="LOGGED_IN")
            {
                string message=FridaWhatsAppWorker.Value(root,"message");
                object matches;
                if(root.TryGetValue("matches",out matches)&&matches is IEnumerable&&!(matches is string))
                {
                    var options=((IEnumerable)matches).OfType<Dictionary<string,object>>().ToArray();
                    if(status=="ambiguous_chat")message="Encontrei "+options.Length+" conversas para \""+group+"\". Responda com o número da opção:";
                    message+="\n"+string.Join("\n",options.Select(ChoiceLine));
                }
                return receipt.Fail(status,string.IsNullOrWhiteSpace(message)?"O leitor nao confirmou o resultado ("+status+").":message);
            }
            if(operation=="status"||operation=="pair")
            {
                object auth;if(!root.TryGetValue("authenticated",out auth)||!object.Equals(auth,true))return receipt.Fail("auth_unconfirmed","Sessao nao confirmada.");
                receipt.Check("authenticated",true,"true","true","browser_dom");
                return receipt.Finish(FridaResultStatus.Verified,"authenticated_profile",operation=="pair"?"WhatsApp pareado e sessao salva.":"A sessao do leitor do WhatsApp esta conectada.");
            }
            string key=operation=="list_groups"||operation=="list_chats"?"chats":operation=="read_unread"?"unread_chats":operation=="search"||operation=="search_cache"?"results":"messages";
            object raw;if(!root.TryGetValue(key,out raw)||!(raw is IEnumerable)||raw is string||raw is IDictionary)return receipt.Fail("missing_records","O leitor nao forneceu os registros solicitados.");
            var values=((IEnumerable)raw).Cast<object>().ToArray();
            if(values.Any(x=>!(x is Dictionary<string,object>)))return receipt.Fail("invalid_records","Registros malformados.");
            if(key=="results"||key=="messages")
            {
                foreach(var item in values.Cast<Dictionary<string,object>>())
                {
                    if(status=="cached")continue;
                    object verified;if(!item.TryGetValue("source_verified",out verified)||!object.Equals(verified,true)||string.IsNullOrWhiteSpace(FridaWhatsAppWorker.Value(item,"chat_id"))||string.IsNullOrWhiteSpace(FridaWhatsAppWorker.Value(item,"message_id"))||!Regex.IsMatch(FridaWhatsAppWorker.Value(item,"reference"),@"^M[a-f0-9]{16}$"))return receipt.Fail("identity_unverified","Mensagem sem identidade de origem verificavel.");
                    // The reader resolves partial names ("custos de reforma", "qualidade de corte e dobra") and verifies the opened header;
                    // here the result must still match the significant tokens of the requested name.
                    if(!string.IsNullOrWhiteSpace(group)&&!SmartChatNameMatch(FridaWhatsAppWorker.Value(item,"chat"),group))return receipt.Fail("chat_mismatch","O resultado pertence a outra conversa.");
                }
            }
            var coverage=root.ContainsKey("coverage")?root["coverage"] as Dictionary<string,object>:null;
            if(coverage==null)return receipt.Fail("missing_coverage","Cobertura da busca nao informada.");
            bool complete=coverage.ContainsKey("complete")&&object.Equals(coverage["complete"],true);
            if(status=="success"&&!complete)return receipt.Fail("invalid_coverage","Leitura incompleta apresentada como completa.");
            receipt.Check("reader_protocol",true,"v2","v2","owned_worker");
            receipt.Check("record_structure",true,"valid","valid","local_parser");
            receipt.Put("retrieved_at_utc",DateTime.UtcNow.ToString("o"));
            return receipt.Finish(status=="partial"?FridaResultStatus.Partial:FridaResultStatus.Verified,complete?"accessible_web_search":"partial_snapshot",complete?"Consulta concluída nos resultados acessíveis do WhatsApp Web.":"Resultados parciais; a cobertura total não foi confirmada.");
        }
        // "2. Mônica PCP — Ontem / Obrigado": the option number is what the operator answers.
        private static string ChoiceLine(Dictionary<string,object> match)
        {
            string name=FridaWhatsAppWorker.Value(match,"name"),preview=FridaWhatsAppWorker.Value(match,"preview");
            if(!string.IsNullOrEmpty(name)&&preview.StartsWith(name,StringComparison.Ordinal))preview=preview.Substring(name.Length).TrimStart(' ','/');
            if(preview.Length>80)preview=preview.Substring(0,80)+"...";
            bool self=match.ContainsKey("self")&&object.Equals(match["self"],true);
            return FridaWhatsAppWorker.Value(match,"candidate")+". "+(string.IsNullOrEmpty(name)?preview:name+(self?" (você)":"")+(preview.Length>0?" — "+preview:""));
        }
        // "de hoje", "de ontem", "de 02/11/2026" or "de 02/11 a 04/11/2026" (date_to is exclusive).
        internal static string PeriodLabel(string from,string to)
        {
            DateTimeOffset start,end;
            if(!DateTimeOffset.TryParse(from,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out start)
                ||!DateTimeOffset.TryParse(to,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out end)||start>=end)return "do período";
            var local=TimeSpan.FromHours(-3);
            DateTime first=start.ToOffset(local).Date,last=end.ToOffset(local).AddTicks(-1).Date,today=FridaWhatsAppPlan.LocalToday().Date;
            if(first!=last)return "de "+first.ToString("dd/MM",System.Globalization.CultureInfo.InvariantCulture)+" a "+last.ToString("dd/MM/yyyy",System.Globalization.CultureInfo.InvariantCulture);
            if(first==today)return "de hoje";
            if(first==today.AddDays(-1))return "de ontem";
            if(first==today.AddDays(-2))return "de anteontem";
            return "de "+first.ToString("dd/MM/yyyy",System.Globalization.CultureInfo.InvariantCulture);
        }
        private static string ReplyV2(FridaToolReceipt receipt,Dictionary<string,object> root)
        {
            if(receipt.Operation=="status"||receipt.Operation=="pair")return receipt.Summary;
            string key=receipt.Operation=="list_groups"||receipt.Operation=="list_chats"?"chats":receipt.Operation=="read_unread"?"unread_chats":receipt.Operation=="search"||receipt.Operation=="search_cache"?"results":"messages";
            var rows=((IEnumerable)root[key]).OfType<Dictionary<string,object>>().ToArray();
            bool cached=FridaWhatsAppWorker.Value(root,"status")=="cached";
            var lines=new List<string>();
            if(cached)lines.Add("Dados do cache local; esta etapa nao atualizou o WhatsApp.");
            else if(!receipt.Data.ContainsKey("senior_spoken"))lines.Add(receipt.Summary);
            object summary,note,focus;
            if(receipt.Data.TryGetValue("search_focus",out focus))lines.Add(Convert.ToString(focus));
            if(receipt.Data.TryGetValue("senior_summary",out summary))lines.Add(Convert.ToString(summary));
            else
            {
                if(receipt.Data.TryGetValue("interpretation_note",out note))lines.Add(Convert.ToString(note));
                foreach(var row in rows.Take(20))
                {
                    Func<string,string> v=k=>FridaWhatsAppWorker.Value(row,k);
                    string text=key=="chats"||key=="unread_chats"?v("name")+": "+v("snippet"):"["+v("reference")+"] "+v("chat")+" — "+v("sender")+" ("+v("time")+"): "+v("text")+(cached?" (coletado: "+v("fetched_at")+")":"");
                    lines.Add(text.Length>1200?text.Substring(0,1200)+" [trecho]":text);
                }
            }
            if(rows.Length==0 && !receipt.Data.ContainsKey("senior_summary"))
            {
                lines.Add("Nenhum registro retornou neste escopo de consulta.");
                // Linked devices keep only part of the phone's history (WhatsApp's permanent
                // "Sincronizando mensagens mais antigas" note); say so only when it matters.
                if(!cached)lines.Add("A busca cobre o histórico disponível no WhatsApp Web; mensagens antigas que ficaram apenas no celular podem não aparecer.");
            }
            object warnings;
            if(root.TryGetValue("attribution_warnings",out warnings)&&warnings is IEnumerable&&!(warnings is string))
                foreach(var warning in ((IEnumerable)warnings).OfType<Dictionary<string,object>>())
                {
                    string chat=FridaWhatsAppWorker.Value(warning,"chat");
                    if(FridaWhatsAppWorker.Value(warning,"status")=="homonymous")lines.Add("Atenção: "+FridaWhatsAppWorker.Value(warning,"conversations")+" conversas se chamam \""+chat+"\"; os resultados indicam o nome exibido, sem distinguir qual delas.");
                    else lines.Add("A unicidade do nome \""+chat+"\" não foi verificada nesta consulta.");
                }
            var coverage=(Dictionary<string,object>)root["coverage"];
            object issueList;
            bool syncing=FridaWhatsAppWorker.Value(coverage,"reason")=="history_sync_in_progress"
                ||(coverage.TryGetValue("issues",out issueList)&&issueList is IEnumerable&&!(issueList is string)&&((IEnumerable)issueList).Cast<object>().Any(x=>Convert.ToString(x)=="history_sync_in_progress"));
            if(syncing)lines.Add("O WhatsApp ainda está sincronizando mensagens antigas com o celular; a busca pode não incluir todo o histórico. Repita a consulta mais tarde.");
            if(receipt.Operation=="read_recent_groups")
                lines.Add("Amostra "+PeriodLabel(FridaWhatsAppWorker.Value(coverage,"date_from"),FridaWhatsAppWorker.Value(coverage,"date_to"))+": "+rows.Length+" mensagens recuperadas em "+FridaWhatsAppWorker.Value(coverage,"groups_read")+" grupos conferidos. Foram considerados até seis grupos recentes e até dez mensagens de cada um no período.");
            else lines.Add("Cobertura: "+FridaWhatsAppWorker.Value(coverage,"scope")+"; estado: "+FridaWhatsAppWorker.Value(coverage,"reason")+". Registros recebidos: "+rows.Length+".");
            if(!string.IsNullOrEmpty(FridaWhatsAppWorker.Value(root,"cursor")))lines.Add("Diga 'continue' para retomar esta pesquisa.");
            return string.Join("\n",lines);
        }
        public static string Reply(FridaToolReceipt receipt,string request)
        {
            if(receipt.Status!=FridaResultStatus.Verified&&receipt.Status!=FridaResultStatus.Partial)return receipt.Output;
            object version;object reader;
            if(receipt.Data.TryGetValue("reader_result",out reader)&&((Dictionary<string,object>)reader).TryGetValue("schema_version",out version)&&Convert.ToInt32(version)==2)return ReplyV2(receipt,(Dictionary<string,object>)reader);
            if(receipt.Operation=="pair")return receipt.Summary;
            var root=(Dictionary<string,object>)receipt.Data["reader_result"];
            if(receipt.Operation=="status")return "A sessao do leitor do WhatsApp esta conectada.";
            string key=receipt.Operation=="list_groups"?"chats":receipt.Operation=="read_unread"?"unread_chats":receipt.Operation=="search"?"results":"messages";
            var items=((IEnumerable)root[key]).Cast<object>().OfType<Dictionary<string,object>>().Take(20).ToArray();
            bool cached=Convert.ToString(receipt.Data["source"])=="local_cache_snapshot";
            var lines=new List<string>{cached?"Dados do cache local (data da coleta em cada registro).":"Grupos e mensagens carregados nesta consulta do WhatsApp."};
            if(root.ContainsKey("group"))lines.Add("Grupo retornado: "+Convert.ToString(root["group"])+".");
            if(items.Length==0){lines.Add("Nenhum registro retornou nesta consulta. Isso nao comprova ausencia de mensagens em todo o historico.");return string.Join("\n",lines);}
            if(Regex.IsMatch(FridaInteractionPolicy.Normalize(request),@"\b(objetivo|finalidade|proposito)\b"))lines.Add("A finalidade do grupo nao foi confirmada: esta consulta contem mensagens, sem uma descricao verificada do grupo.");
            if(Regex.IsMatch(FridaInteractionPolicy.Normalize(request),@"\b(postei|enviei|publiquei)\b"))lines.Add("A autoria das mensagens deve ser conferida pelos remetentes abaixo; o leitor nao certifica quais foram enviadas por voce.");
            int index=0;foreach(var item in items)
            {
                Func<string,string> value=name=>item.ContainsKey(name)?Convert.ToString(item[name]):"";
                string text=receipt.Operation=="list_groups" || receipt.Operation=="read_unread"?value("name")+": "+value("snippet"):value("sender")+" ["+value("time")+"]: "+value("text")+(cached?" (coletado: "+value("fetched_at")+")":"");
                lines.Add("[M"+(++index)+"] "+(text.Length>700?text.Substring(0,700)+" [trecho]":text));
            }
            lines.Add("Escopo: somente os registros retornados nesta consulta.");return string.Join("\n",lines);
        }
    }
}
