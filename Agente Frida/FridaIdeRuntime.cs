using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public sealed class FridaIdeProject
    {
        public string Id { get; set; }
        public string DeletedUtc { get; set; }
        public string Name { get; set; }
        public string Root { get; set; }
        public string SshAlias { get; set; }
        public override string ToString() { return Name + (string.IsNullOrEmpty(SshAlias) ? " · Computador" : " · " + SshAlias); }
    }

    public sealed class FridaArtifactRecord
    {
        public string ConversationId;
        public string Id;
        public string RequestId;
        public string ProjectRoot;
        public string Path;
        public string Destination;
        public string Project;
        public string Operation;
        public string LastUsedUtc;
        public bool Favorite;
        public override string ToString()
        {
            string action = Operation == "write" || Operation == "append" || Operation == "patch" ? "Arquivo salvo" : Operation == "mkdir" ? "Pasta criada" : Operation == "list" || Operation == "inspect" ? "Pasta acessada" : "Arquivo consultado";
            return (Favorite ? "★ " : "") + action + " · " + Destination + " · " + Path;
        }
    }

    public sealed class FridaIdeMeeting
    {
        public string ConversationId;
        public string Id;
        public string RequestId;
        public FridaIdeProject Project;
        public string Topic;
        public string Report;
        public string StartedUtc;
        public string FinishedUtc;
        public List<TeamMessage> Messages;
        public override string ToString() { return FridaIdeRuntime.DisplayTime(StartedUtc) + " · " + Topic; }
    }

    public sealed class FridaIdeContext
    {
        public string ConversationId;
        public string ProjectId;
        public FridaWorkspaceSection Section;
        public FridaConversationKind Kind;
        public FridaConversationStore Store;
        public string RequestId = Guid.NewGuid().ToString("N");
        public FridaIdeProject Project;
        public bool Silent;
        public byte[] Audio;
    }

    public static class FridaIdeRuntime
    {
        private static readonly AsyncLocal<FridaIdeContext> Current = new AsyncLocal<FridaIdeContext>();
        private static readonly object Sync = new object();
        private static readonly List<string> Lines = new List<string>();
        public static FridaIdeContext Context { get { return Current.Value; } }
        public static string DefaultDirectory
        {
            get
            {
                if (Context != null && Context.Project != null && !string.IsNullOrWhiteSpace(Context.Project.Root))
                    return Context.Project.Root;
                if (Context != null)
                    return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }
        public static bool Silent { get { return Context != null && Context.Silent; } }
        public static event Action<string> Changed;
        public static event Action<FridaArtifactRecord> ArtifactChanged;
        public static FridaIdeProject ActiveProject;
        public static string ActiveConversationId;
        public static string HotkeyWarning;
        public static string StorageWarning;
        public static event Action PairingRequested;
        public static event Action<string> PairingFinished;
        public static void FinishPairing(string status) { var handler = PairingFinished; if (handler != null) handler(status); }
        public static void RequestPairing() { var handler = PairingRequested; if (handler != null) handler(); }
        public static readonly string DataRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IdeData");
        public static IDisposable Enter(FridaIdeContext context)
        {
            var scope = new ContextScope(Current.Value);
            Current.Value = context;
            return scope;
        }
        private sealed class ContextScope : IDisposable
        {
            private readonly FridaIdeContext _previous;
            public ContextScope(FridaIdeContext previous) { _previous = previous; }
            public void Dispose() { Current.Value = _previous; }
        }
        public static string DisplayTime(string value)
        {
            DateTime timestamp;
            return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out timestamp)
                ? timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : value ?? "";
        }
        public static string Redact(string text)
        {
            string value = Regex.Replace(text ?? "", @"\b(?:AIza[0-9A-Za-z_-]{25,}|(?:sk-|gsk_)[0-9A-Za-z_-]{20,})", "[credencial ocultada]");
            value = Regex.Replace(value, @"(?i)(Bearer\s+)[A-Za-z0-9._~+/-]+=*", "$1[credencial ocultada]");
            value = Regex.Replace(value, @"(?i)((?:password|senha|api[_-]?key|access[_-]?token|secret)\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s,;]+)", "$1[credencial ocultada]");
            return value;
        }
        public static void Publish(string text)
        {
            FridaDiagnostics.Publish("execution", text);
            var context = Context;
            string line = DateTime.Now.ToString("HH:mm:ss") + " · " +
                (context == null || context.Project == null ? "Computador" : context.Project.ToString()) +
                (context == null ? "" : " · " + context.RequestId.Substring(0, Math.Min(8, context.RequestId.Length))) + "\r\n" + Redact(text);
            lock (Sync)
            {
                Lines.Add(line);
                if (Lines.Count > 1000) Lines.RemoveAt(0);
                try
                {
                    Directory.CreateDirectory(DataRoot);
                    string log = Path.Combine(DataRoot, "terminal.jsonl");
                    if (File.Exists(log) && new FileInfo(log).Length > 4 * 1024 * 1024)
                    {
                        string previous = log + ".previous";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(log, previous);
                    }
                    File.AppendAllText(log, new JavaScriptSerializer().Serialize(new {
                        at_utc = DateTime.UtcNow.ToString("o"), request_id = context == null ? null : context.RequestId,
                        project = context == null ? null : context.Project, text = line
                    }) + "\n", new UTF8Encoding(false));
                }
                catch (IOException ex) { BootLogger.Log("Falha ao salvar terminal: " + ex.GetType().Name); }
                catch (UnauthorizedAccessException ex) { BootLogger.Log("Falha ao salvar terminal: " + ex.GetType().Name); }
            }
            var changed = Changed;
            if (changed != null) foreach (Action<string> handler in changed.GetInvocationList())
                try { handler(line); } catch (InvalidOperationException) { }
        }
        public static string Snapshot()
        {
            lock (Sync)
            {
                if (Lines.Count == 0)
                {
                    string log = Path.Combine(DataRoot, "terminal.jsonl");
                    if (File.Exists(log))
                    {
                        foreach (string row in File.ReadLines(log).Reverse().Take(500).Reverse())
                            try
                            {
                                var record = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(row);
                                object text;
                                if (record != null && record.TryGetValue("text", out text) && text is string) Lines.Add((string)text);
                            }
                            catch (ArgumentException) { }
                            catch (InvalidOperationException) { }
                    }
                }
                return string.Join("\r\n", Lines);
            }
        }
        internal static T LoadState<T>(string path, Func<T> empty) where T : class
        {
            if (!File.Exists(path)) return empty();
            try
            {
                if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Registro local excede o limite de leitura.");
                return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)) ?? empty();
            }
            catch (IOException ex) { BootLogger.Log("Registro local indisponível: " + ex.GetType().Name); }
            catch (UnauthorizedAccessException ex) { BootLogger.Log("Registro local indisponível: " + ex.GetType().Name); }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is InvalidOperationException) && !(ex is InvalidCastException)) throw;
                string backup = path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
                try { File.Move(path, backup); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                StorageWarning = "Alguns registros locais estavam inválidos. Os arquivos originais foram preservados para recuperação.";
                BootLogger.Log("Registro local inválido preservado: " + Path.GetFileName(path) + "; " + ex.GetType().Name);
            }
            return empty();
        }
        public static List<FridaIdeProject> LoadProjects()
        {
            return LoadState(Path.Combine(DataRoot, "projects.json"), () => new List<FridaIdeProject>())
                .Where(project => project != null && !string.IsNullOrWhiteSpace(project.Root)).ToList();
        }
        public static void SaveProjects(List<FridaIdeProject> projects)
        {
            Directory.CreateDirectory(DataRoot);
            string path = Path.Combine(DataRoot, "projects.json"), temporary = path + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(projects), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        public static void SaveMeeting(FridaIdeMeeting meeting)
        {
            string directory = Path.Combine(DataRoot, "Meetings");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, meeting.Id + ".json"), temporary = path + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(meeting), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        public static List<FridaIdeMeeting> LoadMeetings()
        {
            var result = new List<FridaIdeMeeting>();
            string directory = Path.Combine(DataRoot, "Meetings");
            if (!Directory.Exists(directory)) return result;
            foreach (string path in Directory.GetFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(200))
            {
                try { var meeting = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<FridaIdeMeeting>(File.ReadAllText(path, Encoding.UTF8)); if (meeting != null) result.Add(meeting); }
                catch (Exception ex) { BootLogger.Log("Registro de reunião indisponível: " + ex.GetType().Name); }
            }
            return result;
        }
        public static List<FridaArtifactRecord> Artifacts()
        {
            lock (Sync)
            {
                string path = Path.Combine(DataRoot, "artifacts.json");
                if (!File.Exists(path)) return new List<FridaArtifactRecord>();
                return LoadState(path, () => new List<FridaArtifactRecord>()).Where(item => item != null && !string.IsNullOrWhiteSpace(item.Path) && !string.IsNullOrWhiteSpace(item.Destination)).ToList();
            }
        }
        private static void SaveArtifacts(List<FridaArtifactRecord> items)
        {
            Directory.CreateDirectory(DataRoot);
            string path = Path.Combine(DataRoot, "artifacts.json"), temporary = path + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(items), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        public static void RecordArtifact(string path, string operation)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                lock (Sync)
                {
                    var project = Context == null ? null : Context.Project;
                    string destination = project == null || string.IsNullOrEmpty(project.SshAlias) ? "Computador" : project.SshAlias;
                    var items = Artifacts();
                    var item = items.FirstOrDefault(entry => entry.Path.Equals(path, destination == "Computador" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && entry.Destination == destination);
                    if (item == null) { item = new FridaArtifactRecord { Path = path, Destination = destination }; items.Add(item); }
                    if (string.IsNullOrEmpty(item.Id)) item.Id = Guid.NewGuid().ToString("N");
                    item.ConversationId = Context == null ? null : Context.ConversationId;
                    item.RequestId = Context == null ? null : Context.RequestId; item.ProjectRoot = project == null ? null : project.Root;
                    item.Operation = operation; item.Project = project == null ? null : project.Name; item.LastUsedUtc = DateTime.UtcNow.ToString("o");
                    SaveArtifacts(items.OrderByDescending(entry => entry.Favorite).ThenByDescending(entry => entry.LastUsedUtc).Take(2000).ToList());
                    var observer = ArtifactChanged;
                    if (observer != null) foreach (Action<FridaArtifactRecord> handler in observer.GetInvocationList())
                        try { handler(item); } catch (Exception ex) { BootLogger.Log("Evento de arquivo indisponível: " + ex.GetType().Name); }
                }
            }
            catch (Exception ex) { BootLogger.Log("Falha ao registrar arquivo usado: " + ex.GetType().Name); }
        }
        public static void FavoriteArtifact(FridaArtifactRecord item)
        {
            lock (Sync)
            {
                var items = Artifacts(); var stored = items.FirstOrDefault(entry => entry.Path == item.Path && entry.Destination == item.Destination);
                if (stored == null) return; stored.Favorite = !stored.Favorite; SaveArtifacts(items);
            }
        }
        public static void RelocateArtifacts(string destination, string source, string target, char separator)
        {
            lock (Sync)
            {
                var items = Artifacts();
                foreach (var item in items.Where(entry => entry.Destination == destination && (entry.Path.Equals(source, destination == "Computador" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || entry.Path.StartsWith(source + separator, destination == "Computador" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))))
                    item.Path = target + item.Path.Substring(source.Length);
                SaveArtifacts(items);
            }
        }
        private static object Scrub(object value)
        {
            if (value is string) return Redact((string)value);
            var dictionary = value as Dictionary<string, object>;
            if (dictionary != null)
                return dictionary.ToDictionary(pair => pair.Key, pair => Regex.IsMatch(pair.Key, @"(?i)^(password|senha|secret|api[_-]?key|access[_-]?token)$") ? (object)"[credencial ocultada]" : Scrub(pair.Value));
            var sequence = value as System.Collections.IEnumerable;
            if (sequence != null) return sequence.Cast<object>().Select(Scrub).ToArray();
            return value;
        }
        public static void RecordExecution(FridaToolReceipt receipt)
        {
            FridaDiagnostics.Publish("execution", receipt.Tool + " · " + receipt.Status + " · " + (receipt.FinishedUtc - receipt.StartedUtc).TotalMilliseconds.ToString("0") + " ms · " + receipt.Summary, receipt.OperationId);
            var context = Context;
            if (context == null) return;
            try
            {
                lock (Sync)
                {
                    string directory = Path.Combine(DataRoot, "Executions"); Directory.CreateDirectory(directory);
                    var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                    object payload = serializer.DeserializeObject(receipt.ToJson());
                    string destination = context.Project == null || string.IsNullOrEmpty(context.Project.SshAlias) ? "Computador" : context.Project.SshAlias;
                    var artifact = Artifacts().FirstOrDefault(item => item.Path.Equals(receipt.Target, destination == "Computador" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && item.Destination == destination);
                    string path = Path.Combine(directory, receipt.OperationId + ".json"), temporary = path + ".tmp";
                    File.WriteAllText(temporary, serializer.Serialize(new { request_id = context.RequestId, project = context.Project, artifact_id = artifact == null ? null : artifact.Id, receipt = Scrub(payload) }), new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                }
            }
            catch (Exception ex) { BootLogger.Log("Registro de execução indisponível: " + ex.GetType().Name); }
        }
        public static List<FridaArtifactRecord> ArtifactsForRequest(string requestId)
        {
            var result = new List<FridaArtifactRecord>();
            var artifacts = Artifacts();
            string directory = Path.Combine(DataRoot, "Executions");
            if (!Directory.Exists(directory)) return result;
            foreach (string file in Directory.GetFiles(directory, "*.json"))
            {
                try
                {
                    var record = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
                    if (Convert.ToString(record["request_id"]) != requestId) continue;
                    object id;
                    if (!record.TryGetValue("artifact_id", out id) || id == null) continue;
                    var artifact = artifacts.FirstOrDefault(item => item.Id == Convert.ToString(id));
                    if (artifact != null && !result.Any(item => item.Id == artifact.Id)) result.Add(artifact);
                }
                catch (Exception ex) { BootLogger.Log("Vínculo de arquivo indisponível: " + ex.GetType().Name); }
            }
            return result;
        }
        public static string ExecutionDetails(string requestId)
        {
            string directory = Path.Combine(DataRoot, "Executions");
            if (!Directory.Exists(directory)) return "";
            var text = new StringBuilder();
            foreach (string file in Directory.GetFiles(directory, "*.json").OrderBy(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var record = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
                    if (Convert.ToString(record["request_id"]) != requestId) continue;
                    var receipt = record["receipt"] as Dictionary<string, object>;
                    if (receipt == null) continue;
                    text.AppendLine(Convert.ToString(receipt["tool"]) + " · " + Convert.ToString(receipt["operation"]) + " · " + Convert.ToString(receipt["target"]));
                    text.AppendLine(Convert.ToString(receipt["summary"]));
                    object output; if (receipt.TryGetValue("untrusted_output", out output)) text.AppendLine(Convert.ToString(output));
                }
                catch (Exception ex) { BootLogger.Log("Detalhe de execução indisponível: " + ex.GetType().Name); }
            }
            return text.ToString();
        }
        public static string ProjectInstructions()
        {
            var context = Context;
            if (context == null || context.Project == null) return "";
            return "\nProjeto ativo: " + context.Project.Name + "; raiz: " + context.Project.Root +
                (FridaSshWorkspace.IsRemote ? "; servidor SSH: " + context.Project.SshAlias + ". As ferramentas de código e arquivos e run_powershell estão roteadas para Linux remoto; execute comandos sh/POSIX nesse destino." : "; ambiente Windows local.") +
                " Use a raiz informada para o pedido. Cada operação precisa ser confirmada pela ferramenta." + ProjectMemory(context);
        }
        // The other conversations of the project (including ones moved into it) give this request context.
        private static string ProjectMemory(FridaIdeContext context)
        {
            if (context.Store == null || string.IsNullOrEmpty(context.ProjectId)) return "";
            try { return context.Store.ProjectMemory(context.ProjectId, context.ConversationId); }
            catch (Exception ex) { BootLogger.Log("Memória do projeto indisponível: " + ex.GetType().Name); return ""; }
        }
        public static string LocalCommand(string command)
        {
            var context = Context;
            if (context == null || context.Project == null) return command;
            if (!string.IsNullOrEmpty(context.Project.SshAlias)) throw new InvalidOperationException("Este executor requer projeto local.");
            return "Set-Location -LiteralPath '" + context.Project.Root.Replace("'", "''") + "'; " + command;
        }
        public static string ResolvePath(string path)
        {
            var context = Context;
            if (FridaSshWorkspace.IsRemote) return FridaSshWorkspace.ResolvePath(path);
            if (context == null || context.Project == null || Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(path)) return path;
            return Path.GetFullPath(Path.Combine(context.Project.Root, path));
        }
    }
}
