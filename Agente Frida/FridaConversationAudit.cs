using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public sealed class FridaConversationEntry
    {
        public int schema_version=1;
        public string conversation_id, project_id;
        public string input_channel="unknown",resolved_request;
        public string request_id,started_utc,finished_utc,status="processing",request,response,model_analysis,spoken_text,routing,routing_model,execution_json,error_code;
        public string speech_status="not_started",speech_error;
        public string routing_status,routing_response;
        public string project_name, project_root, destination, audio_path, audio_error;
        public int speech_chunks;
        public string[] references=new string[0];
    }
    public static class FridaConversationAudit
    {
        private static readonly object Sync=new object();
        public static string DirectoryPath {get{return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ConversationReports");}}
        private static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=16*1024*1024};}
        public static event Action<FridaConversationEntry> Saved;
        public static FridaConversationEntry Start(string id,string request)
        {
            var entry = new FridaConversationEntry { request_id = id, request = Redact(request), started_utc = DateTime.UtcNow.ToString("o") };
            var context = FridaIdeRuntime.Context;
            if (context != null)
            {
                context.RequestId = id;
                entry.conversation_id = context.ConversationId;
                entry.project_id = context.ProjectId ?? (context.Project == null ? null : context.Project.Id);
                if (context.Project != null) { entry.project_name = context.Project.Name; entry.project_root = context.Project.Root; entry.destination = context.Project.SshAlias ?? "Computador"; }
                if (context.Audio != null)
                {
                    try
                    {
                        string directory = Path.Combine(FridaIdeRuntime.DataRoot, "Audio"); Directory.CreateDirectory(directory);
                        string path = Path.Combine(directory, id + ".wav");
                        File.WriteAllBytes(path, context.Audio); entry.audio_path = path;
                    }
                    catch (Exception ex) { entry.audio_error = ex.GetType().Name; }
                }
            }
            return entry;
        }
        private static string Redact(string text)
        {return text==null?null:Regex.Replace(text,@"\b(?:AIza[0-9A-Za-z_-]{25,}|(?:sk-|gsk_)[0-9A-Za-z_-]{20,})","[credencial ocultada]");}
        private static string PathFor(FridaConversationEntry entry)
        {
            DateTime started=DateTime.Parse(entry.started_utc,null,System.Globalization.DateTimeStyles.RoundtripKind);
            if(!Regex.IsMatch(entry.request_id ?? "",@"^[a-f0-9]{32}$"))throw new InvalidDataException("Identificador de atendimento invalido.");
            return Path.Combine(DirectoryPath,started.ToUniversalTime().ToString("yyyyMMdd-HHmmss-fff")+"-"+entry.request_id+".json");
        }
        public static string Save(FridaConversationEntry entry)
        {
            lock(Sync)
            {
                string temporary=null;
                try
                {
                    Directory.CreateDirectory(DirectoryPath);string path=PathFor(entry);temporary=path+".tmp";
                    entry.request=Redact(entry.request);entry.resolved_request=Redact(entry.resolved_request);entry.response=Redact(entry.response);entry.model_analysis=Redact(entry.model_analysis);entry.spoken_text=Redact(entry.spoken_text);
                    entry.execution_json=Redact(entry.execution_json);
                    entry.routing_response=Redact(entry.routing_response);
                    File.WriteAllText(temporary,Serializer().Serialize(entry),new UTF8Encoding(false));
                    var check=Serializer().Deserialize<FridaConversationEntry>(File.ReadAllText(temporary,Encoding.UTF8));
                    if(check==null || check.request_id!=entry.request_id)throw new InvalidDataException("Registro de conversa nao conferido.");
                    if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
                    if (!string.IsNullOrEmpty(check.conversation_id))
                        (FridaIdeRuntime.Context != null && FridaIdeRuntime.Context.Store != null ? FridaIdeRuntime.Context.Store : new FridaConversationStore(Path.Combine(FridaIdeRuntime.DataRoot, "Workspace"))).RecordReport(check);
                    var handler = Saved;
                    if (handler != null) foreach (Action<FridaConversationEntry> observer in handler.GetInvocationList())
                        try { observer(check); } catch (Exception ex) { BootLogger.Log("Atualização de conversa indisponível: " + ex.GetType().Name); }
                    return path;
                }
                catch(Exception ex){BootLogger.Log("Falha ao registrar conversa: "+ex.GetType().Name);return null;}
                finally{if(temporary!=null)try{if(File.Exists(temporary))File.Delete(temporary);}catch{}}
            }
        }
        public static void RecordSpeech(string path,FridaSpeechResult speech)
        {
            if(string.IsNullOrWhiteSpace(path) || speech==null)return;
            lock(Sync)
            {
                try
                {
                    var entry=Serializer().Deserialize<FridaConversationEntry>(File.ReadAllText(path,Encoding.UTF8));
                    if(entry==null)return;entry.speech_status=speech.Status;entry.speech_error=speech.ErrorCode;entry.speech_chunks=speech.CompletedChunks;Save(entry);
                }
                catch(Exception ex){BootLogger.Log("Falha ao registrar entrega de voz: "+ex.GetType().Name);}
            }
        }
        public static FridaConversationEntry LoadLatest()
        {
            lock(Sync)
            {
                try
                {
                    if(!Directory.Exists(DirectoryPath))return null;
                    foreach(string path in Directory.GetFiles(DirectoryPath,"*.json").OrderByDescending(Path.GetFileName).Take(10))
                        try{var entry=Serializer().Deserialize<FridaConversationEntry>(File.ReadAllText(path,Encoding.UTF8));if(entry!=null && entry.schema_version==1)return entry;}catch{}
                }
                catch(Exception ex){BootLogger.Log("Falha ao consultar conversa: "+ex.GetType().Name);}
                return null;
            }
        }
    }
    public sealed class FridaSpeechResult
    {
        public string Status="not_started",ErrorCode;
        public int CompletedChunks;
    }
}
