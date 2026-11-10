using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public enum FridaWorkspaceSection { Chat, Code }
    public enum FridaConversationKind { Conversation, Meeting }

    public sealed class FridaConversation
    {
        public string Id, Title, ProjectId, OriginConversationId, CreatedUtc, UpdatedUtc;
        public string DeletedUtc, DeletedWithProject, Draft = "";
        public FridaWorkspaceSection Section;
        public FridaConversationKind Kind;
        public bool ManualSection;
        public List<string> Attachments = new List<string>();
        public override string ToString() { return Title; }
    }

    public sealed class FridaConversationMessage
    {
        public string Id, ConversationId, RequestId, Author, Role, Model, Text, AtUtc;
        public string Status = "complete", Channel, ArtifactId;
    }

    public sealed class FridaWorkspaceCatalog
    {
        public int Version = 1;
        public List<FridaConversation> Conversations = new List<FridaConversation>();
        public List<FridaIdeProject> Projects = new List<FridaIdeProject>();
        public Dictionary<string, string> Imported = new Dictionary<string, string>();
    }

    // Metadata commits atomically. Each message is an independently atomic file:
    // an interrupted append cannot corrupt the previous conversation or its index.
    // The API pages records and never imposes a permanent transcript-size limit.
    public sealed partial class FridaConversationStore
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, int> Active = new Dictionary<string, int>();
        private readonly string _root;
        public string Root { get { return _root; } }
        private string CatalogPath { get { return Path.Combine(_root, "workspace.json"); } }
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }; }
        public FridaConversationStore(string root)
        {
            string full = Path.GetFullPath(root);
            _root = full.Length > Path.GetPathRoot(full).Length ? full.TrimEnd('\\', '/') : full;
        }
        // .NET Framework 4.8 here runs with MAX_PATH (260). Message files are the deepest paths
        // (root + Conversations\<id>\Messages\<ticks>-<id>.json), so the temporary name stays short:
        // the shared AtomicWrite suffix (37 chars) left only ~13 chars of margin in the real install.
        internal const int MaxSafeRootLength = 125;
        internal static void AtomicWrite(string path, string text)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public bool RootTooDeep { get { return _root.Length > MaxSafeRootLength; } }
        private static string Now() { return DateTime.UtcNow.ToString("o"); }
        private static string NewId() { return Guid.NewGuid().ToString("N"); }
        private static void CheckId(string id)
        {
            if (!Regex.IsMatch(id ?? "", "^[a-f0-9]{32}$")) throw new ArgumentException("Identificador de conversa ou mensagem inválido.");
        }
        private string MessageDirectory(string id) { CheckId(id); return Path.Combine(_root, "Conversations", id, "Messages"); }
        // During a migration batch (always under Gate on one thread) the catalog lives in memory and is
        // flushed periodically; identities are deterministic, so an interrupted batch repeats safely.
        private FridaWorkspaceCatalog _batch;
        private bool _batchDirty;
        private void FlushBatch()
        {
            if (_batch == null || !_batchDirty) return;
            var state = _batch; _batch = null; _batchDirty = false;
            try { Write(state); } finally { _batch = state; }
        }
        private FridaWorkspaceCatalog Read()
        {
            if (_batch != null) return _batch;
            if (!File.Exists(CatalogPath)) return new FridaWorkspaceCatalog();
            // Never turn a corrupt catalog into an empty writable catalog.
            FridaWorkspaceCatalog state;
            try { state = Json().Deserialize<FridaWorkspaceCatalog>(File.ReadAllText(CatalogPath, Encoding.UTF8)); }
            catch (ArgumentException ex) { throw new InvalidDataException("Catálogo de conversas ilegível. O original foi preservado.", ex); }
            catch (InvalidOperationException ex) { throw new InvalidDataException("Catálogo de conversas ilegível. O original foi preservado.", ex); }
            if (state == null || state.Version != 1 || state.Conversations == null || state.Projects == null || state.Imported == null)
                throw new InvalidDataException("Catálogo de conversas inválido ou incompatível. O original foi preservado.");
            return state;
        }
        private string PreviousCatalogPath { get { return Path.Combine(_root, "workspace.previous.json"); } }
        private void Write(FridaWorkspaceCatalog state)
        {
            if (_batch != null) { _batch = state; _batchDirty = true; return; }
            Directory.CreateDirectory(_root);
            // Every Write follows a successful Read under Gate, so the file being replaced is valid:
            // keep it as the last known good catalog for recovery from a later corruption.
            // Best effort: a locked backup must not block saving the conversation itself.
            try { if (File.Exists(CatalogPath)) File.Copy(CatalogPath, PreviousCatalogPath, true); }
            catch (IOException ex) { BootLogger.Log("Cópia de segurança do catálogo indisponível: " + ex.GetType().Name); }
            catch (UnauthorizedAccessException ex) { BootLogger.Log("Cópia de segurança do catálogo indisponível: " + ex.GetType().Name); }
            AtomicWrite(CatalogPath, Json().Serialize(state));
        }
        // Explicit, user-confirmed recovery: the invalid catalog is kept beside the restored copy.
        public string RestorePreviousCatalog()
        {
            lock (Gate)
            {
                if (!File.Exists(PreviousCatalogPath)) throw new FileNotFoundException("Não há cópia anterior do catálogo para restaurar.");
                var previous = Json().Deserialize<FridaWorkspaceCatalog>(File.ReadAllText(PreviousCatalogPath, Encoding.UTF8));
                if (previous == null || previous.Version != 1 || previous.Conversations == null || previous.Projects == null || previous.Imported == null)
                    throw new InvalidDataException("A cópia anterior do catálogo também está inválida.");
                string preserved = CatalogPath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + NewId();
                if (File.Exists(CatalogPath)) File.Move(CatalogPath, preserved);
                AtomicWrite(CatalogPath, File.ReadAllText(PreviousCatalogPath, Encoding.UTF8));
                return preserved;
            }
        }
        private static FridaConversation Find(FridaWorkspaceCatalog state, string id, bool includeTrash)
        {
            CheckId(id);
            var item = state.Conversations.FirstOrDefault(c => c.Id == id);
            if (item == null || (!includeTrash && item.DeletedUtc != null)) throw new InvalidOperationException("Conversa indisponível.");
            return item;
        }
        public List<FridaConversation> List(FridaWorkspaceSection? section = null, bool trash = false, string search = "")
        {
            string query = (search ?? "").Trim();
            lock (Gate) return Read().Conversations.Where(c => (c.DeletedUtc != null) == trash && (!section.HasValue || c.Section == section.Value)
                && (query.Length == 0 || Contains(c.Title, query) || (query.Length >= 3 && Contains(SearchText(c.Id), query))))
                .OrderByDescending(c => c.UpdatedUtc).ToList();
        }
        private static bool Contains(string text, string query)
        {
            return System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(text ?? "", query,
                System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;
        }
        // Content search reads each conversation once; PutMessage invalidates its entry and the
        // file count catches messages written by another store instance in this process.
        private static readonly Dictionary<string, KeyValuePair<int, string>> SearchCache = new Dictionary<string, KeyValuePair<int, string>>();
        private const int SearchTextLimit = 2 * 1024 * 1024;
        private string SearchText(string id)
        {
            string directory = MessageDirectory(id);
            if (!Directory.Exists(directory)) return "";
            var files = Directory.GetFiles(directory, "*.json");
            KeyValuePair<int, string> cached;
            if (SearchCache.TryGetValue(ActiveKey(id), out cached) && cached.Key == files.Length) return cached.Value;
            var text = new StringBuilder();
            foreach (string file in files.OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                if (text.Length > SearchTextLimit) break;
                try { var m = Json().Deserialize<FridaConversationMessage>(File.ReadAllText(file, Encoding.UTF8)); if (m != null) text.Append(m.Text).Append('\n'); }
                catch (ArgumentException) { } // A damaged message must not hide the other results.
                catch (IOException) { }
            }
            string value = text.ToString(); SearchCache[ActiveKey(id)] = new KeyValuePair<int, string>(files.Length, value); return value;
        }
        public FridaConversation Get(string id, bool includeTrash = false) { lock (Gate) return Find(Read(), id, includeTrash); }
        public FridaConversation TryGet(string id)
        {
            if (!Regex.IsMatch(id ?? "", "^[a-f0-9]{32}$")) return null;
            lock (Gate) return Read().Conversations.FirstOrDefault(c => c.Id == id && c.DeletedUtc == null);
        }
        // Resolves the conversation that holds an attendance, live or imported.
        public string ConversationForRequest(string requestId)
        {
            if (string.IsNullOrEmpty(requestId)) return null;
            lock (Gate)
            {
                var state = Read(); string id;
                if (!state.Imported.TryGetValue("request:" + requestId, out id)) return null;
                var c = state.Conversations.FirstOrDefault(x => x.Id == id);
                return c == null || c.DeletedUtc != null ? null : c.Id;
            }
        }
        public void RenameProject(string id, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Informe um nome.");
            lock (Gate)
            {
                var s = Read(); var p = s.Projects.FirstOrDefault(x => x.Id == id && x.DeletedUtc == null);
                if (p == null) throw new InvalidOperationException("Projeto indisponível.");
                p.Name = name.Trim(); Write(s);
            }
        }
        public FridaConversation Create(string title, FridaWorkspaceSection section, FridaConversationKind kind, string projectId = null, string originId = null)
        {
            lock (Gate)
            {
                var state = Read();
                if (projectId != null && !state.Projects.Any(p => p.Id == projectId && p.DeletedUtc == null)) throw new InvalidOperationException("Projeto indisponível.");
                if (originId != null) Find(state, originId, false);
                var item = new FridaConversation { Id = NewId(), Title = string.IsNullOrWhiteSpace(title) ? "Nova conversa" : title.Trim(), Section = section,
                    Kind = kind, ProjectId = projectId, OriginConversationId = originId, CreatedUtc = Now(), UpdatedUtc = Now() };
                state.Conversations.Add(item); Write(state); return item;
            }
        }
        public void UpdateDraft(string id, string draft, IEnumerable<string> attachments)
        {
            lock (Gate) { var s = Read(); var c = Find(s, id, false); c.Draft = draft ?? ""; c.Attachments = (attachments ?? new string[0]).Distinct().ToList(); Write(s); }
        }
        // A long paste becomes a text file of the conversation (or of the workspace while no
        // conversation exists yet), attached to the draft like any other file.
        public string SavePastedText(string conversationId, string text)
        {
            string folder;
            if (conversationId == null) folder = Path.Combine(_root, "Anexos");
            else { CheckId(conversationId); folder = Path.Combine(_root, "Conversations", conversationId, "Anexos"); }
            Directory.CreateDirectory(folder);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss"), path = Path.Combine(folder, "colado-" + stamp + ".txt");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, "colado-" + stamp + "-" + i + ".txt");
            AtomicWrite(path, text ?? "");
            return path;
        }
        public void Rename(string id, string title)
        {
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Informe um título.");
            lock (Gate) { var s = Read(); var c = Find(s, id, false); c.Title = title.Trim(); c.UpdatedUtc = Now(); Write(s); }
        }
        public void Move(string id, FridaWorkspaceSection section)
        {
            lock (Gate) { var s = Read(); EnsureIdle(id); var c = Find(s, id, false); c.Section = section; c.ManualSection = true; Write(s); }
        }
        // Puts an existing conversation into a project (null takes it out). From then on its history is
        // part of the project memory that every request in that project receives.
        public void AssignProject(string id, string projectId)
        {
            lock (Gate)
            {
                var s = Read(); EnsureIdle(id); var c = Find(s, id, false);
                if (projectId != null && !s.Projects.Any(p => p.Id == projectId && p.DeletedUtc == null)) throw new ArgumentException("Projeto inexistente ou na Lixeira.");
                c.ProjectId = projectId; c.UpdatedUtc = Now(); Write(s);
            }
        }
        // What the other conversations of a project established: title and latest exchange of each,
        // newest first, without tool output, within a character budget.
        public string ProjectMemory(string projectId, string exceptConversationId, int budget = 4000)
        {
            if (string.IsNullOrEmpty(projectId)) return "";
            List<FridaConversation> others;
            lock (Gate) others = Read().Conversations.Where(c => c.ProjectId == projectId && c.DeletedUtc == null && c.Id != exceptConversationId).OrderByDescending(c => c.UpdatedUtc).Take(8).ToList();
            var text = new StringBuilder();
            foreach (var c in others)
            {
                FridaBrainConversationState state;
                try { state = LoadBrainState(c.Id); } catch (IOException) { continue; } catch (ArgumentException) { continue; } catch (InvalidOperationException) { continue; }
                string request = FridaReplyParts.Answer(state.LastRequest, 280), reply = FridaReplyParts.Answer(state.LastResponse, 600);
                if (request.Length == 0 && reply.Length == 0) continue;
                string item = "- \"" + c.Title + "\": pedido \"" + request + "\"" + (reply.Length > 0 ? " → resposta: " + reply : "") + "\n";
                if (text.Length + item.Length > budget) break;
                text.Append(item);
            }
            return text.Length == 0 ? "" : "\n[MEMÓRIA DO PROJETO] Outras conversas deste projeto, das mais recentes para as mais antigas. Use como contexto e não repita ações já concluídas:\n" + text;
        }
        public FridaIdeProject RegisterProject(string name, string root, string sshAlias = null)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Informe a pasta do projeto.");
            root = string.IsNullOrEmpty(sshAlias) ? Path.GetFullPath(root) : root;
            lock (Gate)
            {
                var s = Read();
                var item = s.Projects.FirstOrDefault(p => (p.SshAlias ?? "") == (sshAlias ?? "") && string.Equals(p.Root.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'),
                    string.IsNullOrEmpty(sshAlias) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                if (item != null) { if (item.DeletedUtc != null) throw new InvalidOperationException("Restaure o projeto na Lixeira."); return item; }
                item = new FridaIdeProject { Id = NewId(), Name = name, Root = root, SshAlias = sshAlias };
                s.Projects.Add(item); Write(s); return item;
            }
        }
        public List<FridaIdeProject> Projects(bool trash = false) { lock (Gate) return Read().Projects.Where(p => (p.DeletedUtc != null) == trash).ToList(); }
        private string ActiveKey(string id) { return _root.ToUpperInvariant() + "/" + id; }
        private void EnsureIdle(string id) { if (Active.ContainsKey(ActiveKey(id))) throw new InvalidOperationException("Interrompa ou aguarde a execução antes de alterar este item."); }
        public IDisposable BeginExecution(string id)
        {
            lock (Gate) { Find(Read(), id, false); string key = ActiveKey(id); int count; Active.TryGetValue(key, out count); Active[key] = count + 1; return new Lease(key); }
        }
        private sealed class Lease : IDisposable
        {
            private string _key;
            public Lease(string key) { _key = key; }
            public void Dispose() { lock (Gate) { if (_key == null) return; if (--Active[_key] == 0) Active.Remove(_key); _key = null; } }
        }
        public void Trash(string id)
        {
            lock (Gate) { var s = Read(); EnsureIdle(id); var c = Find(s, id, false); c.DeletedUtc = Now(); c.DeletedWithProject = null; Write(s); }
        }
        public void Restore(string id)
        {
            lock (Gate)
            {
                var s = Read(); var c = Find(s, id, true);
                if (c.ProjectId != null && s.Projects.Any(p => p.Id == c.ProjectId && p.DeletedUtc != null)) throw new InvalidOperationException("Restaure primeiro o projeto.");
                c.DeletedUtc = null; c.DeletedWithProject = null; Write(s);
            }
        }
        public void TrashProject(string id)
        {
            lock (Gate)
            {
                var s = Read(); var p = s.Projects.FirstOrDefault(x => x.Id == id && x.DeletedUtc == null);
                if (p == null) throw new InvalidOperationException("Projeto indisponível.");
                var children = s.Conversations.Where(c => c.ProjectId == id && c.DeletedUtc == null).ToList();
                foreach (var c in children) EnsureIdle(c.Id);
                p.DeletedUtc = Now(); foreach (var c in children) { c.DeletedUtc = p.DeletedUtc; c.DeletedWithProject = id; } Write(s);
            }
        }
        public void RestoreProject(string id)
        {
            lock (Gate)
            {
                var s = Read(); var p = s.Projects.FirstOrDefault(x => x.Id == id);
                if (p == null) throw new InvalidOperationException("Projeto indisponível.");
                p.DeletedUtc = null;
                foreach (var c in s.Conversations.Where(c => c.DeletedWithProject == id)) { c.DeletedUtc = null; c.DeletedWithProject = null; }
                Write(s);
            }
        }
        public void PutMessage(FridaConversationMessage message)
        {
            lock (Gate)
            {
                var s = Read(); var c = Find(s, message.ConversationId, false);
                if (string.IsNullOrEmpty(message.Id)) message.Id = NewId(); CheckId(message.Id);
                if (string.IsNullOrEmpty(message.AtUtc)) message.AtUtc = Now();
                message.Text = FridaIdeRuntime.Redact(message.Text);
                string directory = MessageDirectory(c.Id); Directory.CreateDirectory(directory);
                string path = Directory.EnumerateFiles(directory, "*-" + message.Id + ".json").FirstOrDefault();
                if (path == null)
                {
                    DateTime at = DateTime.Parse(message.AtUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                    path = Path.Combine(directory, at.Ticks.ToString("D19") + "-" + message.Id + ".json");
                }
                AtomicWrite(path, Json().Serialize(message));
                SearchCache.Remove(ActiveKey(c.Id));
                // The catalog is rewritten only when the ordering timestamp actually moves.
                if (string.CompareOrdinal(message.AtUtc, c.UpdatedUtc) > 0) { c.UpdatedUtc = message.AtUtc; Write(s); }
            }
        }
        public List<FridaConversationMessage> Messages(string id, int offset = 0, int count = 100, bool includeTrash = false)
        {
            if (offset < 0 || count < 1 || count > 500) throw new ArgumentOutOfRangeException("count");
            lock (Gate)
            {
                Find(Read(), id, includeTrash); string directory = MessageDirectory(id);
                if (!Directory.Exists(directory)) return new List<FridaConversationMessage>();
                return Directory.EnumerateFiles(directory, "*.json").OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).Skip(offset).Take(count)
                    .Select(path => Json().Deserialize<FridaConversationMessage>(File.ReadAllText(path, Encoding.UTF8)))
                    .Where(m => m != null && m.ConversationId == id).ToList();
            }
        }
        public int MessageCount(string id)
        { lock (Gate) { Find(Read(), id, false); string directory = MessageDirectory(id); return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Count() : 0; } }
        public bool HasRequest(string id, string requestId)
        {
            lock (Gate) { Find(Read(), id, false); string directory = MessageDirectory(id); return Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*-" + StableId(requestId + ":user") + ".json").Any(); }
        }
    }
}
