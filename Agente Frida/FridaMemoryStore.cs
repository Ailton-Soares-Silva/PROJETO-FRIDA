using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public sealed class FridaMemoryEntry
    {
        public string Id { get; set; }
        public string Type { get; set; } // perfil_operador, preferencia, correcao_recebida, fato_ambiente, decisao_projeto
        public string Title { get; set; }
        public string Content { get; set; }
        public string SourceConversationId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public List<string> Tags { get; set; }

        public FridaMemoryEntry()
        {
            Id = "mem_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            Type = "fato_ambiente";
            Title = "";
            Content = "";
            CreatedUtc = DateTime.UtcNow;
            UpdatedUtc = DateTime.UtcNow;
            Tags = new List<string>();
        }

        public string ToIndexLine()
        {
            return "[" + Id + "] (" + Type + "): " + (string.IsNullOrWhiteSpace(Title) ? (Content.Length > 80 ? Content.Substring(0, 80) + "..." : Content) : Title);
        }
    }

    public static class FridaMemoryStore
    {
        private static readonly object Sync = new object();
        private static string _memoryDir;
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private static readonly Dictionary<string, FridaMemoryEntry> _cache = new Dictionary<string, FridaMemoryEntry>(StringComparer.OrdinalIgnoreCase);
        private static bool _initialized = false;

        public static string MemoryDirectory
        {
            get
            {
                if (_memoryDir == null)
                {
                    string root = FridaIdeRuntime.DataRoot ?? AppDomain.CurrentDomain.BaseDirectory;
                    _memoryDir = Path.Combine(root, "Memory");
                }
                return _memoryDir;
            }
        }

        public static void Initialize(string customDir = null)
        {
            lock (Sync)
            {
                if (_initialized && string.IsNullOrEmpty(customDir)) return;
                if (!string.IsNullOrEmpty(customDir)) _memoryDir = customDir;
                Directory.CreateDirectory(MemoryDirectory);
                _cache.Clear();

                // Load all existing memories
                foreach (string file in Directory.EnumerateFiles(MemoryDirectory, "mem_*.json"))
                {
                    try
                    {
                        string text = File.ReadAllText(file, Encoding.UTF8);
                        var entry = _json.Deserialize<FridaMemoryEntry>(text);
                        if (entry != null && !string.IsNullOrEmpty(entry.Id))
                        {
                            _cache[entry.Id] = entry;
                        }
                    }
                    catch { }
                }

                // If empty, seed initial operator profile and environment memories
                if (_cache.Count == 0)
                {
                    SeedDefaults();
                }

                _initialized = true;
            }
        }

        private static void SeedDefaults()
        {
            SaveInternal(new FridaMemoryEntry
            {
                Id = "mem_operator_profile",
                Type = "perfil_operador",
                Title = "Operador Ailton Soares (dj)",
                Content = "Ailton Soares (dj) é o operador e criador da Frida na bancada privada. Usuário técnico avançado que valoriza precisão, verificação comprovada com recibos e entregas diretas sem sermões ou filtros excessivos.",
                Tags = new List<string> { "operador", "dj", "ailton", "perfil" }
            });

            SaveInternal(new FridaMemoryEntry
            {
                Id = "mem_env_windows11",
                Type = "fato_ambiente",
                Title = "Ambiente Windows 11 com PowerShell, .NET e Python",
                Content = "Sistema operacional Windows 11 x64 nativo. Possui compilador C# (csc.exe v4.0.30319), PowerShell 5.1, Python 3.12 com bibliotecas PyPDF2, openpyxl, sqlite3 e ferramentas do Projeto Frida.",
                Tags = new List<string> { "windows11", "ambiente", "python", "dotnet", "csc" }
            });

            SaveInternal(new FridaMemoryEntry
            {
                Id = "mem_pref_verifications",
                Type = "preferencia",
                Title = "Preferencia por validacoes reais e recibos",
                Content = "O operador exige que açoes sejam comprovadas com testes funcionais reais, compilacoes bem-sucedidas e recibos de execucao do sistema. Nunca inventar ou declarar sucesso sem evidencia.",
                Tags = new List<string> { "recibos", "verificacao", "testes", "qualidade" }
            });
        }

        private static void SaveInternal(FridaMemoryEntry entry)
        {
            _cache[entry.Id] = entry;
            string path = Path.Combine(MemoryDirectory, entry.Id + ".json");
            File.WriteAllText(path, _json.Serialize(entry), Encoding.UTF8);
        }

        public static FridaMemoryEntry Write(string type, string title, string content, string conversationId = null, IEnumerable<string> tags = null)
        {
            Initialize();
            lock (Sync)
            {
                var entry = new FridaMemoryEntry
                {
                    Type = string.IsNullOrWhiteSpace(type) ? "fato_ambiente" : type.Trim().ToLowerInvariant(),
                    Title = (title ?? "").Trim(),
                    Content = (content ?? "").Trim(),
                    SourceConversationId = conversationId,
                    Tags = tags != null ? tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList() : new List<string>()
                };

                SaveInternal(entry);
                return entry;
            }
        }

        public static bool Update(string id, string content, string title = null)
        {
            Initialize();
            lock (Sync)
            {
                FridaMemoryEntry entry;
                if (!_cache.TryGetValue(id, out entry)) return false;

                if (!string.IsNullOrWhiteSpace(content)) entry.Content = content.Trim();
                if (!string.IsNullOrWhiteSpace(title)) entry.Title = title.Trim();
                entry.UpdatedUtc = DateTime.UtcNow;

                SaveInternal(entry);
                return true;
            }
        }

        public static bool Forget(string id)
        {
            Initialize();
            lock (Sync)
            {
                if (_cache.Remove(id))
                {
                    string path = Path.Combine(MemoryDirectory, id + ".json");
                    if (File.Exists(path)) File.Delete(path);
                    return true;
                }
                return false;
            }
        }

        public static FridaMemoryEntry Get(string id)
        {
            Initialize();
            lock (Sync)
            {
                FridaMemoryEntry entry;
                _cache.TryGetValue(id, out entry);
                return entry;
            }
        }

        public static List<FridaMemoryEntry> ListAll()
        {
            Initialize();
            lock (Sync)
            {
                return _cache.Values.OrderByDescending(x => x.UpdatedUtc).ToList();
            }
        }

        public static List<FridaMemoryEntry> Search(string query, string typeFilter = null, int maxResults = 5)
        {
            Initialize();
            lock (Sync)
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    return _cache.Values
                        .Where(m => string.IsNullOrEmpty(typeFilter) || string.Equals(m.Type, typeFilter, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(m => m.UpdatedUtc)
                        .Take(maxResults)
                        .ToList();
                }

                string[] terms = FridaInteractionPolicy.Normalize(query)
                    .Split(new char[] { ' ', ',', '.', ';', '?', '!' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length >= 3)
                    .ToArray();

                var scored = new List<KeyValuePair<FridaMemoryEntry, int>>();

                foreach (var m in _cache.Values)
                {
                    if (!string.IsNullOrEmpty(typeFilter) && !string.Equals(m.Type, typeFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    int score = 0;
                    string normTitle = FridaInteractionPolicy.Normalize(m.Title);
                    string normContent = FridaInteractionPolicy.Normalize(m.Content);

                    foreach (string t in terms)
                    {
                        if (normTitle.Contains(t)) score += 5;
                        if (normContent.Contains(t)) score += 2;
                        if (m.Tags.Any(tag => tag.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)) score += 4;
                    }

                    if (score > 0)
                    {
                        scored.Add(new KeyValuePair<FridaMemoryEntry, int>(m, score));
                    }
                }

                return scored.OrderByDescending(x => x.Value)
                    .ThenByDescending(x => x.Key.UpdatedUtc)
                    .Select(x => x.Key)
                    .Take(maxResults)
                    .ToList();
            }
        }

        public static FridaMemoryEntry WriteMemory(string type, string title, string content, string conversationId = null, string tags = null)
        {
            var tagList = tags != null ? tags.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries) : null;
            return Write(type, title, content, conversationId, tagList);
        }

        public static FridaMemoryEntry ReadMemory(string id)
        {
            return Get(id);
        }

        public static bool ForgetMemory(string id)
        {
            return Forget(id);
        }

        public static List<FridaMemoryEntry> ListMemories()
        {
            return ListAll();
        }

        public static List<FridaMemoryEntry> Search(string query, int maxResults)
        {
            return Search(query, null, maxResults);
        }

        public static string GetOneLineIndex(int maxEntries = 12)
        {
            Initialize();
            lock (Sync)
            {
                var list = _cache.Values
                    .OrderByDescending(m => m.Type == "perfil_operador" ? 3 : (m.Type == "preferencia" ? 2 : 1))
                    .ThenByDescending(m => m.UpdatedUtc)
                    .Take(maxEntries)
                    .Select(m => m.ToIndexLine())
                    .ToArray();

                return string.Join("\n", list);
            }
        }

        public static string GetRelevantMemoriesSummary(string context, int maxEntries = 5)
        {
            Initialize();
            var matches = Search(context, null, maxEntries);
            if (matches.Count == 0) return GetOneLineIndex(3);

            var sb = new StringBuilder();
            foreach (var m in matches)
            {
                sb.AppendLine("- [" + m.Id + "] (" + m.Type + "): " + m.Content);
            }
            return sb.ToString().Trim();
        }

        public static FridaToolReceipt ExecuteToolAction(Dictionary<string, object> args, string conversationId)
        {
            string op = (args != null && args.ContainsKey("operation") ? Convert.ToString(args["operation"]) : "list").Trim().ToLowerInvariant();
            var receipt = new FridaToolReceipt("memory", op, "system_memory", op == "write" || op == "update" || op == "forget");

            try
            {
                if (op == "list")
                {
                    var all = ListAll();
                    receipt.Put("count", all.Count);
                    receipt.Put("memories", all.Select(m => new { m.Id, m.Type, m.Title, m.Tags, m.UpdatedUtc }).ToArray());
                    receipt.Check("memory_listed", true, "true", "true", "memory_store");
                    return receipt.Finish(FridaResultStatus.Verified, "list", "Listadas " + all.Count + " memorias registradas.", GetOneLineIndex(20));
                }

                if (op == "search")
                {
                    string q = args.ContainsKey("query") ? Convert.ToString(args["query"]) : "";
                    string tf = args.ContainsKey("type") ? Convert.ToString(args["type"]) : null;
                    var results = Search(q, tf, 5);
                    receipt.Put("query", q);
                    receipt.Put("results_count", results.Count);
                    receipt.Put("results", results.Select(r => new { r.Id, r.Type, r.Title, r.Content }).ToArray());
                    receipt.Check("search_executed", true, "true", "true", "memory_store");
                    string resText = results.Count == 0 ? "Nenhuma memoria encontrada para: " + q : string.Join("\n\n", results.Select(r => "[" + r.Id + "] (" + r.Type + ") " + r.Title + "\n" + r.Content).ToArray());
                    return receipt.Finish(FridaResultStatus.Verified, "search", "Busca em memorias concluida (" + results.Count + " encontradas).", resText);
                }

                if (op == "read")
                {
                    string memId = args.ContainsKey("memory_id") ? Convert.ToString(args["memory_id"]).Trim() : "";
                    var entry = Get(memId);
                    if (entry == null) return receipt.Fail("not_found", "Memoria nao encontrada: " + memId);

                    receipt.Put("memory", entry);
                    receipt.Check("read_ok", true, "true", "true", "memory_store");
                    return receipt.Finish(FridaResultStatus.Verified, "read", "Memoria carregada: " + entry.Title, entry.Content);
                }

                if (op == "write")
                {
                    string type = args.ContainsKey("type") ? Convert.ToString(args["type"]) : "fato_ambiente";
                    string title = args.ContainsKey("title") ? Convert.ToString(args["title"]) : "";
                    string content = args.ContainsKey("content") ? Convert.ToString(args["content"]) : "";
                    if (string.IsNullOrWhiteSpace(content)) return receipt.Fail("empty_content", "O conteudo da memoria nao pode ser vazio.");

                    var created = Write(type, title, content, conversationId);
                    receipt.Put("created_id", created.Id);
                    receipt.Check("memory_written", true, "true", "true", "memory_store");
                    return receipt.Finish(FridaResultStatus.Verified, "write", "Memoria registrada com sucesso: [" + created.Id + "] " + created.Title, created.Content);
                }

                if (op == "update")
                {
                    string memId = args.ContainsKey("memory_id") ? Convert.ToString(args["memory_id"]).Trim() : "";
                    string content = args.ContainsKey("content") ? Convert.ToString(args["content"]) : "";
                    string title = args.ContainsKey("title") ? Convert.ToString(args["title"]) : null;

                    bool ok = Update(memId, content, title);
                    if (!ok) return receipt.Fail("not_found", "Memoria nao encontrada para atualizar: " + memId);

                    receipt.Check("memory_updated", true, "true", "true", "memory_store");
                    return receipt.Finish(FridaResultStatus.Verified, "update", "Memoria " + memId + " atualizada com sucesso.", content);
                }

                if (op == "forget")
                {
                    string memId = args.ContainsKey("memory_id") ? Convert.ToString(args["memory_id"]).Trim() : "";
                    bool ok = Forget(memId);
                    if (!ok) return receipt.Fail("not_found", "Memoria nao encontrada para apagar: " + memId);

                    receipt.Check("memory_forgotten", true, "true", "true", "memory_store");
                    return receipt.Finish(FridaResultStatus.Verified, "forget", "Memoria " + memId + " apagada com sucesso.", "");
                }

                return receipt.Fail("invalid_operation", "Operacao de memoria desconhecida: " + op);
            }
            catch (Exception ex)
            {
                return receipt.Fail("memory_exception", "Falha na operacao de memoria: " + ex.Message);
            }
        }
    }
}
