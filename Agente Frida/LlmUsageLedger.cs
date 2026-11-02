// language: C# 5 (.NET Framework 4.8), file: LlmUsageLedger.cs, target: Windows 10/11 x64
// Registro persistente e real do consumo de cada modelo (requisicoes, falhas, tokens, latencia,
// limites informados pelos provedores via cabecalhos x-ratelimit-* e cooldowns de cota).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public class UsageDayTotals
    {
        public string date = "";
        public int requests = 0;
        public int failures = 0;
        public long tokens_in = 0;
        public long tokens_out = 0;
    }

    public class UsageEvent
    {
        public string at = "";
        public string bucket = "";
        public string model = "";
        public bool ok = false;
        public int code = 0;
        public double latency_ms = 0;
        public long tok_in = 0;
        public long tok_out = 0;
        public string note = "";
    }

    /// <summary>DTO persistido em llm_usage.json.</summary>
    public class ModelUsageSnapshot
    {
        public string bucket = "";
        public string model = "";
        public string day = "";
        public int req_today = 0;
        public int fail_today = 0;
        public int rate_limited_today = 0;
        public long tok_in_today = 0;
        public long tok_out_today = 0;
        public long req_total = 0;
        public long fail_total = 0;
        public long tok_in_total = 0;
        public long tok_out_total = 0;
        public double last_latency_ms = 0;
        public double avg_latency_ms = 0;
        public string last_used = "";
        public string last_error = "";
        public string last_error_at = "";
        public string cooldown_until_utc = "";
        public string provider_limit_note = "";
        public Dictionary<string, string> rate_limits = new Dictionary<string, string>();
        public string rate_limits_at = "";
        public List<UsageDayTotals> history = new List<UsageDayTotals>();
    }

    public class UsageStoreFile
    {
        public string saved_at = "";
        public List<ModelUsageSnapshot> models = new List<ModelUsageSnapshot>();
        public List<UsageEvent> events = new List<UsageEvent>();
    }

    public class ModelRuntimeStats
    {
        public string Bucket;
        public string ModelId;
        public string FriendlyName;
        public string CharacterTitle;
        public string CharacterRole;
        public string SpriteKey;

        public string DayKey = DateTime.Now.ToString("yyyy-MM-dd");
        public int TotalRequestsToday;
        public int FailuresToday;
        public int RateLimitedToday;
        public long TokensInToday;
        public long TokensOutToday;
        public long TotalRequestsAllTime;
        public long FailuresAllTime;
        public long TokensInAllTime;
        public long TokensOutAllTime;

        public List<DateTime> RequestHistoryUtc = new List<DateTime>();
        public DateTime CooldownUntilUtc = DateTime.MinValue;
        public string LastErrorReason = "";
        public DateTime LastErrorLocal = DateTime.MinValue;
        public DateTime LastUsedLocal = DateTime.MinValue;
        public bool IsActiveNow = false;
        public double LastLatencyMs = 0;
        public double AvgLatencyMs = 0;
        public string ProviderLimitNote = "";
        public Dictionary<string, string> RateLimits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public DateTime RateLimitsAtLocal = DateTime.MinValue;
        public List<UsageDayTotals> History = new List<UsageDayTotals>();

        public string ProviderLabel { get { return ModelQuotaGuard.ProviderLabel(Bucket); } }
        public string FunctionLabel { get { return ModelQuotaGuard.FunctionLabel(Bucket); } }
        public string Key { get { return (Bucket ?? "default") + ":" + ModelId; } }

        /// <summary>Limite diario real (cabecalhos do provedor) ou nota capturada do ultimo erro de cota.</summary>
        public string DailyQuotaDesc
        {
            get
            {
                long rem, lim;
                if (TryGetDailyRequestQuota(out rem, out lim))
                {
                    return string.Format(CultureInfo.GetCultureInfo("pt-BR"), "{0:N0} de {1:N0} req/dia restantes", rem, lim);
                }
                if (!string.IsNullOrEmpty(ProviderLimitNote)) return ProviderLimitNote;
                return string.Format("{0} req hoje (provedor não informa limite)", TotalRequestsToday);
            }
        }

        public string HourlyQuotaDesc
        {
            get
            {
                string d = DescribeProviderLimits();
                return string.IsNullOrEmpty(d) ? string.Format("{0} req na última hora", GetRequestsInLastHour()) : d;
            }
        }

        public int GetRequestsInLastHour()
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-1);
            lock (RequestHistoryUtc)
            {
                RequestHistoryUtc.RemoveAll(delegate(DateTime t) { return t < cutoff; });
                return RequestHistoryUtc.Count;
            }
        }

        public bool IsCooldownActive()
        {
            return DateTime.UtcNow < CooldownUntilUtc;
        }

        public bool IsDisabled
        {
            get { return ModelQuotaGuard.IsDisabled(Bucket, ModelId); }
        }

        public string GetStatusState()
        {
            if (IsActiveNow) return "EM USO";
            if (IsDisabled) return "DESATIVADO";
            if (DateTime.UtcNow < CooldownUntilUtc) return "COTA EXCEDIDA";
            return "PRONTO";
        }

        public TimeSpan GetCooldownRemaining()
        {
            if (DateTime.UtcNow >= CooldownUntilUtc) return TimeSpan.Zero;
            return CooldownUntilUtc - DateTime.UtcNow;
        }

        public bool TryGetDailyRequestQuota(out long remaining, out long limit)
        {
            remaining = 0;
            limit = 0;
            Dictionary<string, string> rl = RateLimits;
            if (rl == null) return false;
            string[] limKeys;
            string[] remKeys;
            if ((Bucket ?? "").IndexOf("groq", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Groq: x-ratelimit-*-requests = requisicoes por dia (RPD)
                limKeys = new string[] { "x-ratelimit-limit-requests-day", "x-ratelimit-limit-requests" };
                remKeys = new string[] { "x-ratelimit-remaining-requests-day", "x-ratelimit-remaining-requests" };
            }
            else
            {
                limKeys = new string[] { "x-ratelimit-limit-requests-day" };
                remKeys = new string[] { "x-ratelimit-remaining-requests-day" };
            }
            for (int i = 0; i < limKeys.Length; i++)
            {
                string l, r;
                lock (rl)
                {
                    if (!rl.TryGetValue(limKeys[i], out l) || !rl.TryGetValue(remKeys[i], out r)) continue;
                }
                if (long.TryParse(l, NumberStyles.Any, CultureInfo.InvariantCulture, out limit) &&
                    long.TryParse(r, NumberStyles.Any, CultureInfo.InvariantCulture, out remaining))
                {
                    return limit > 0;
                }
            }
            return false;
        }

        /// <summary>Resumo legivel de todos os pares x-ratelimit-limit-* / x-ratelimit-remaining-* recebidos.</summary>
        public string DescribeProviderLimits()
        {
            Dictionary<string, string> rl = RateLimits;
            if (rl == null) return "";
            List<string> parts = new List<string>();
            lock (rl)
            {
                foreach (KeyValuePair<string, string> kv in rl)
                {
                    string k = kv.Key.ToLowerInvariant();
                    if (!k.StartsWith("x-ratelimit-limit")) continue;
                    string suffix = k.Substring("x-ratelimit-limit".Length);
                    string rem;
                    rl.TryGetValue("x-ratelimit-remaining" + suffix, out rem);
                    string reset;
                    rl.TryGetValue("x-ratelimit-reset" + suffix, out reset);
                    string txt = LimitLabel(suffix) + ": " + FormatNum(rem ?? "?") + "/" + FormatNum(kv.Value);
                    if (!string.IsNullOrEmpty(reset)) txt += " (reset " + reset + ")";
                    parts.Add(txt);
                }
            }
            return string.Join(" · ", parts.ToArray());
        }

        private string LimitLabel(string suffix)
        {
            string s = (suffix ?? "").ToLowerInvariant();
            bool groq = (Bucket ?? "").IndexOf("groq", StringComparison.OrdinalIgnoreCase) >= 0;
            string what = s.Contains("token") ? "Tokens" : (s.Contains("audio") ? "Áudio(s)" : "Req");
            string per;
            if (s.Contains("day")) per = "/dia";
            else if (s.Contains("minute")) per = "/min";
            else if (s.Contains("hour")) per = "/h";
            else if (groq) per = s.Contains("token") ? "/min" : "/dia";
            else per = "";
            return what + per;
        }

        private static string FormatNum(string raw)
        {
            long n;
            if (long.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out n))
            {
                return n.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"));
            }
            return raw;
        }
    }

    /// <summary>Sinalizador global de encerramento: interrompe cascatas de requisicoes ao sair.</summary>
    public static class FridaShutdown
    {
        public static volatile bool IsShuttingDown = false;
    }

    public static class ModelQuotaGuard
    {
        public static TimeSpan GetCooldownRemaining(string bucket, string model)
        {
            string key = (bucket ?? "default") + ":" + model;
            lock (Sync)
            {
                ModelRuntimeStats s;
                if (Registry.TryGetValue(key, out s)) return s.GetCooldownRemaining();
            }
            return TimeSpan.Zero;
        }

        public static void ResetTodayCounters()
        {
            lock (Sync)
            {
                foreach (KeyValuePair<string, ModelRuntimeStats> kv in Registry)
                {
                    ModelRuntimeStats s = kv.Value;
                    s.TotalRequestsToday = 0;
                    s.FailuresToday = 0;
                    s.RateLimitedToday = 0;
                    s.TokensInToday = 0;
                    s.TokensOutToday = 0;
                }
                _dirty = true;
            }
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, ModelRuntimeStats> Registry = new Dictionary<string, ModelRuntimeStats>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<UsageEvent> RecentEvents = new List<UsageEvent>();
        private const int MaxEvents = 400;
        private static readonly string[] GoogleBuckets = new string[] { "gemini", "vision", "gsearch", "stt" };

        private static FridaSettings _keySource;
        private static string _configPath;
        private static string _storePath;
        private static volatile bool _dirty = false;
        private static Timer _flushTimer;

        // ------------------------------------------------------------------
        // Inicializacao / persistencia
        // ------------------------------------------------------------------
        public static void Initialize(string storePath)
        {
            lock (Sync)
            {
                _storePath = storePath;
                LoadLocked();
            }
            if (_flushTimer == null)
            {
                _flushTimer = new Timer(delegate { if (_dirty) Flush(); }, null, 3000, 3000);
            }
        }

        /// <summary>Vincula as configuracoes para filtrar provedores sem chave e aplicar ativacao/preferencia.</summary>
        public static void BindSettings(FridaSettings settings)
        {
            lock (Sync) { _keySource = settings; }
        }

        public static void BindSettings(FridaSettings settings, string configPath)
        {
            lock (Sync)
            {
                _keySource = settings;
                _configPath = configPath;
            }
        }

        private static void LoadLocked()
        {
            if (string.IsNullOrEmpty(_storePath) || !File.Exists(_storePath)) return;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = 32 * 1024 * 1024;
                UsageStoreFile file = ser.Deserialize<UsageStoreFile>(File.ReadAllText(_storePath, Encoding.UTF8));
                if (file == null) return;
                if (file.models != null)
                {
                    for (int i = 0; i < file.models.Count; i++)
                    {
                        ModelUsageSnapshot snap = file.models[i];
                        if (snap == null || string.IsNullOrEmpty(snap.model)) continue;
                        ModelRuntimeStats s = GetOrCreateLocked(snap.bucket, snap.model);
                        s.DayKey = string.IsNullOrEmpty(snap.day) ? DateTime.Now.ToString("yyyy-MM-dd") : snap.day;
                        s.TotalRequestsToday = snap.req_today;
                        s.FailuresToday = snap.fail_today;
                        s.RateLimitedToday = snap.rate_limited_today;
                        s.TokensInToday = snap.tok_in_today;
                        s.TokensOutToday = snap.tok_out_today;
                        s.TotalRequestsAllTime = snap.req_total;
                        s.FailuresAllTime = snap.fail_total;
                        s.TokensInAllTime = snap.tok_in_total;
                        s.TokensOutAllTime = snap.tok_out_total;
                        s.LastLatencyMs = snap.last_latency_ms;
                        s.AvgLatencyMs = snap.avg_latency_ms;
                        s.LastUsedLocal = ParseLocal(snap.last_used);
                        s.LastErrorReason = snap.last_error ?? "";
                        s.LastErrorLocal = ParseLocal(snap.last_error_at);
                        s.ProviderLimitNote = snap.provider_limit_note ?? "";
                        s.RateLimitsAtLocal = ParseLocal(snap.rate_limits_at);
                        if (snap.rate_limits != null)
                        {
                            foreach (KeyValuePair<string, string> kv in snap.rate_limits) s.RateLimits[kv.Key] = kv.Value;
                        }
                        if (snap.history != null) s.History = snap.history;
                        DateTime cd;
                        if (DateTime.TryParse(snap.cooldown_until_utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out cd) && cd > DateTime.UtcNow)
                        {
                            s.CooldownUntilUtc = cd;
                        }
                        RollDayLocked(s);
                    }
                }
                if (file.events != null)
                {
                    RecentEvents.AddRange(file.events);
                    while (RecentEvents.Count > MaxEvents) RecentEvents.RemoveAt(0);
                }
            }
            catch (Exception ex)
            {
                BootLogger.Log("LlmUsageLedger: falha ao carregar " + _storePath + ": " + ex.Message);
            }
        }

        public static void Flush()
        {
            string path;
            string json;
            lock (Sync)
            {
                path = _storePath;
                if (string.IsNullOrEmpty(path)) return;
                UsageStoreFile file = new UsageStoreFile();
                file.saved_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                foreach (KeyValuePair<string, ModelRuntimeStats> kv in Registry)
                {
                    ModelRuntimeStats s = kv.Value;
                    if (s.TotalRequestsAllTime == 0 && s.FailuresAllTime == 0 && !s.IsCooldownActive()) continue;
                    ModelUsageSnapshot snap = new ModelUsageSnapshot();
                    snap.bucket = s.Bucket;
                    snap.model = s.ModelId;
                    snap.day = s.DayKey;
                    snap.req_today = s.TotalRequestsToday;
                    snap.fail_today = s.FailuresToday;
                    snap.rate_limited_today = s.RateLimitedToday;
                    snap.tok_in_today = s.TokensInToday;
                    snap.tok_out_today = s.TokensOutToday;
                    snap.req_total = s.TotalRequestsAllTime;
                    snap.fail_total = s.FailuresAllTime;
                    snap.tok_in_total = s.TokensInAllTime;
                    snap.tok_out_total = s.TokensOutAllTime;
                    snap.last_latency_ms = s.LastLatencyMs;
                    snap.avg_latency_ms = s.AvgLatencyMs;
                    snap.last_used = FormatLocal(s.LastUsedLocal);
                    snap.last_error = s.LastErrorReason ?? "";
                    snap.last_error_at = FormatLocal(s.LastErrorLocal);
                    snap.cooldown_until_utc = s.IsCooldownActive() ? s.CooldownUntilUtc.ToString("o", CultureInfo.InvariantCulture) : "";
                    snap.provider_limit_note = s.ProviderLimitNote ?? "";
                    lock (s.RateLimits)
                    {
                        snap.rate_limits = new Dictionary<string, string>(s.RateLimits);
                    }
                    snap.rate_limits_at = FormatLocal(s.RateLimitsAtLocal);
                    snap.history = s.History ?? new List<UsageDayTotals>();
                    file.models.Add(snap);
                }
                file.events = new List<UsageEvent>(RecentEvents);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = 32 * 1024 * 1024;
                json = ser.Serialize(file);
                _dirty = false;
            }

            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                try
                {
                    File.Copy(tmp, path, true);
                    try { File.Delete(tmp); } catch { }
                }
                catch
                {
                    File.WriteAllText(path, json, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                BootLogger.Log("LlmUsageLedger: falha ao salvar: " + ex.Message);
            }
        }

        private static string FormatLocal(DateTime dt)
        {
            return dt == DateTime.MinValue ? "" : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static DateTime ParseLocal(string s)
        {
            DateTime dt;
            if (!string.IsNullOrEmpty(s) && DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt;
            return DateTime.MinValue;
        }

        // ------------------------------------------------------------------
        // Rotulos
        // ------------------------------------------------------------------
        public static string ProviderLabel(string bucket)
        {
            string b = (bucket ?? "").ToLowerInvariant();
            if (b.Contains("groq")) return "Groq";
            if (b.Contains("cerebras")) return "Cerebras";
            if (b.Contains("openrouter")) return "OpenRouter";
            if (b.Contains("zai")) return "Zhipu (Z.ai)";
            if (b.Contains("siliconflow") || b.EndsWith("_sf")) return "SiliconFlow";
            return "Google Gemini";
        }

        public static string FunctionLabel(string bucket)
        {
            string b = (bucket ?? "").ToLowerInvariant();
            if (b.StartsWith("stt")) return "Transcrição (voz→texto)";
            if (b.StartsWith("tts")) return b == "tts" ? "Voz da Frida (TTS)" : "Voz da Frida (TTS, chave " + b.Substring(4) + ")";
            if (b.StartsWith("vis") || b == "vision") return "Visão de tela";
            if (b == "gsearch") return "Pesquisa web";
            if (b == "gemini") return "Cérebro (reserva Gemini)";
            return "Cérebro (conversa/ferramentas)";
        }

        private static string SpriteFor(string bucket, string model)
        {
            string b = (bucket ?? "").ToLowerInvariant();
            string m = (model ?? "").ToLowerInvariant();
            if (b.StartsWith("tts")) return m.Contains("lite") ? "oracle" : "priestess";
            if (b.StartsWith("stt")) return "oracle";
            if (b.Contains("groq")) return m.Contains("qwen") ? "tactician" : "scout";
            if (b.Contains("cerebras")) return "titan";
            if (b.Contains("openrouter")) return "herald";
            if (b.Contains("zai")) return "scribe";
            if (b.Contains("siliconflow") || b.EndsWith("_sf")) return "tactician";
            return "scholar";
        }

        private static ModelRuntimeStats GetOrCreateLocked(string bucket, string modelId)
        {
            string key = (bucket ?? "default") + ":" + modelId;
            ModelRuntimeStats stats;
            if (!Registry.TryGetValue(key, out stats))
            {
                stats = new ModelRuntimeStats();
                stats.Bucket = bucket ?? "default";
                stats.ModelId = modelId;
                stats.FriendlyName = ProviderLabel(bucket) + " · " + modelId;
                stats.CharacterTitle = FunctionLabel(bucket);
                stats.CharacterRole = ProviderLabel(bucket);
                stats.SpriteKey = SpriteFor(bucket, modelId);
                Registry[key] = stats;
            }
            return stats;
        }

        /// <summary>Registra um modelo disponivel (aparece no gerenciador mesmo antes do primeiro uso).</summary>
        public static ModelRuntimeStats EnsureRegistered(string bucket, string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return null;
            lock (Sync)
            {
                ModelRuntimeStats s = GetOrCreateLocked(bucket, modelId);
                RollDayLocked(s);
                return s;
            }
        }

        // Compatibilidade com o painel Zelda
        public static ModelRuntimeStats RegisterModelInternal(string bucket, string modelId, string friendlyName, string title, string role, string spriteKey, string dailyQuota, string hourlyQuota)
        {
            return EnsureRegistered(bucket, modelId);
        }

        private static void RollDayLocked(ModelRuntimeStats s)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (s.DayKey == today) return;
            if (s.TotalRequestsToday > 0 || s.FailuresToday > 0)
            {
                UsageDayTotals d = new UsageDayTotals();
                d.date = s.DayKey;
                d.requests = s.TotalRequestsToday;
                d.failures = s.FailuresToday;
                d.tokens_in = s.TokensInToday;
                d.tokens_out = s.TokensOutToday;
                if (s.History == null) s.History = new List<UsageDayTotals>();
                s.History.RemoveAll(delegate(UsageDayTotals x) { return x.date == d.date; });
                s.History.Add(d);
                while (s.History.Count > 30) s.History.RemoveAt(0);
            }
            s.DayKey = today;
            s.TotalRequestsToday = 0;
            s.FailuresToday = 0;
            s.RateLimitedToday = 0;
            s.TokensInToday = 0;
            s.TokensOutToday = 0;
            _dirty = true;
        }

        // ------------------------------------------------------------------
        // Disponibilidade / ativacao / preferencia
        // ------------------------------------------------------------------
        public static bool IsAvailable(string bucket, string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            if (IsDisabled(bucket, model)) return false;
            string key = (bucket ?? "default") + ":" + model;
            lock (Sync)
            {
                ModelRuntimeStats s;
                if (Registry.TryGetValue(key, out s))
                {
                    return DateTime.UtcNow >= s.CooldownUntilUtc;
                }
            }
            return true;
        }

        public static bool IsDisabled(string bucket, string model)
        {
            FridaSettings s = _keySource;
            if (s == null || s.disabled_models == null) return false;
            string key = (bucket ?? "default") + ":" + model;
            for (int i = 0; i < s.disabled_models.Length; i++)
            {
                if (string.Equals(s.disabled_models[i], key, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static void SetDisabled(string bucket, string model, bool disabled)
        {
            FridaSettings s = _keySource;
            if (s == null) return;
            string key = (bucket ?? "default") + ":" + model;
            List<string> list = new List<string>(s.disabled_models ?? new string[0]);
            list.RemoveAll(delegate(string x) { return string.Equals(x, key, StringComparison.OrdinalIgnoreCase); });
            if (disabled) list.Add(key);
            s.disabled_models = list.ToArray();
            SaveSettings();
        }

        /// <summary>0 = mais preferido; int.MaxValue = sem preferencia (mantem a ordem padrao).</summary>
        public static int GetPreferenceRank(string bucket, string model)
        {
            FridaSettings s = _keySource;
            if (s == null || s.preferred_models == null) return int.MaxValue;
            string key = (bucket ?? "default") + ":" + model;
            for (int i = 0; i < s.preferred_models.Length; i++)
            {
                if (string.Equals(s.preferred_models[i], key, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return int.MaxValue;
        }

        public static void MakePreferred(string bucket, string model)
        {
            FridaSettings s = _keySource;
            if (s == null) return;
            string key = (bucket ?? "default") + ":" + model;
            List<string> list = new List<string>(s.preferred_models ?? new string[0]);
            list.RemoveAll(delegate(string x) { return string.Equals(x, key, StringComparison.OrdinalIgnoreCase); });
            list.Insert(0, key);
            s.preferred_models = list.ToArray();
            SaveSettings();
        }

        public static void RemovePreferred(string bucket, string model)
        {
            FridaSettings s = _keySource;
            if (s == null) return;
            string key = (bucket ?? "default") + ":" + model;
            List<string> list = new List<string>(s.preferred_models ?? new string[0]);
            list.RemoveAll(delegate(string x) { return string.Equals(x, key, StringComparison.OrdinalIgnoreCase); });
            s.preferred_models = list.ToArray();
            SaveSettings();
        }

        /// <summary>Ordenacao estavel: modelos preferidos primeiro (pela posicao), o resto mantem a ordem original.</summary>
        public static List<T> OrderByPreference<T>(IList<T> items, Func<T, string> bucketOf, Func<T, string> modelOf)
        {
            List<T> list = new List<T>(items);
            return list
                .Select(delegate(T x, int idx) { return new KeyValuePair<int, KeyValuePair<int, T>>(GetPreferenceRank(bucketOf(x), modelOf(x)), new KeyValuePair<int, T>(idx, x)); })
                .OrderBy(delegate(KeyValuePair<int, KeyValuePair<int, T>> p) { return p.Key; })
                .ThenBy(delegate(KeyValuePair<int, KeyValuePair<int, T>> p) { return p.Value.Key; })
                .Select(delegate(KeyValuePair<int, KeyValuePair<int, T>> p) { return p.Value.Value; })
                .ToList();
        }

        private static void SaveSettings()
        {
            FridaSettings s = _keySource;
            string path = _configPath;
            if (s != null && !string.IsNullOrEmpty(path)) FridaSettings.Save(s, path);
        }

        public static void SetActiveModel(string bucket, string model)
        {
            if (string.IsNullOrEmpty(model)) return;
            lock (Sync)
            {
                ModelRuntimeStats s = GetOrCreateLocked(bucket, model);
                s.IsActiveNow = true;
            }
        }

        public static void ClearActiveModel(string bucket, string model)
        {
            if (string.IsNullOrEmpty(model)) return;
            string key = (bucket ?? "default") + ":" + model;
            lock (Sync)
            {
                ModelRuntimeStats s;
                if (Registry.TryGetValue(key, out s)) s.IsActiveNow = false;
            }
        }

        public static void ClearActiveModel()
        {
            lock (Sync)
            {
                foreach (KeyValuePair<string, ModelRuntimeStats> kvp in Registry) kvp.Value.IsActiveNow = false;
            }
        }

        // ------------------------------------------------------------------
        // Registro de chamadas
        // ------------------------------------------------------------------
        public static void RecordRequestSuccess(string bucket, string model, double latencyMs)
        {
            RecordRequestSuccess(bucket, model, latencyMs, 0, 0, null);
        }

        public static void RecordRequestSuccess(string bucket, string model, double latencyMs, long tokIn, long tokOut, string note)
        {
            if (string.IsNullOrEmpty(model)) return;
            lock (Sync)
            {
                ModelRuntimeStats stats = GetOrCreateLocked(bucket, model);
                RollDayLocked(stats);
                stats.TotalRequestsToday++;
                stats.TotalRequestsAllTime++;
                stats.TokensInToday += tokIn;
                stats.TokensOutToday += tokOut;
                stats.TokensInAllTime += tokIn;
                stats.TokensOutAllTime += tokOut;
                lock (stats.RequestHistoryUtc) stats.RequestHistoryUtc.Add(DateTime.UtcNow);
                stats.LastLatencyMs = latencyMs;
                stats.AvgLatencyMs = stats.AvgLatencyMs <= 0 ? latencyMs : (stats.AvgLatencyMs * 0.8 + latencyMs * 0.2);
                stats.LastUsedLocal = DateTime.Now;
                stats.IsActiveNow = false;
                AddEventLocked(bucket, model, true, 200, latencyMs, tokIn, tokOut, note ?? "");
                _dirty = true;
            }
        }

        /// <summary>Registra uma resposta HTTP (sucesso ou falha) capturando tokens do corpo e limites dos cabecalhos.</summary>
        public static void RecordHttpResponse(string bucket, string model, double latencyMs, HttpResponseMessage resp, string body)
        {
            if (string.IsNullOrEmpty(model) || resp == null) return;
            CaptureRateLimitHeaders(bucket, model, resp);
            int code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                long tIn, tOut;
                ExtractTokenUsage(body, out tIn, out tOut);
                RecordRequestSuccess(bucket, model, latencyMs, tIn, tOut, null);
            }
            else
            {
                TimeSpan? retryAfter = null;
                if (resp.Headers.RetryAfter != null)
                {
                    if (resp.Headers.RetryAfter.Delta.HasValue) retryAfter = resp.Headers.RetryAfter.Delta.Value;
                    else if (resp.Headers.RetryAfter.Date.HasValue) retryAfter = resp.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                }
                ReportHttpFailure(bucket, model, code, body, retryAfter, latencyMs);
            }
        }

        public static void RecordException(string bucket, string model, string message)
        {
            if (string.IsNullOrEmpty(model)) return;
            lock (Sync)
            {
                ModelRuntimeStats stats = GetOrCreateLocked(bucket, model);
                RollDayLocked(stats);
                stats.FailuresToday++;
                stats.FailuresAllTime++;
                stats.LastErrorReason = Truncate("Falha de rede/execução: " + message, 220);
                stats.LastErrorLocal = DateTime.Now;
                stats.IsActiveNow = false;
                AddEventLocked(bucket, model, false, 0, 0, 0, 0, Truncate(message, 160));
                _dirty = true;
            }
        }

        private static void CaptureRateLimitHeaders(string bucket, string model, HttpResponseMessage resp)
        {
            Dictionary<string, string> found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (KeyValuePair<string, IEnumerable<string>> h in resp.Headers)
                {
                    if (h.Key.StartsWith("x-ratelimit", StringComparison.OrdinalIgnoreCase))
                    {
                        found[h.Key.ToLowerInvariant()] = string.Join(",", h.Value.ToArray());
                    }
                }
            }
            catch { }
            if (found.Count == 0) return;

            lock (Sync)
            {
                ModelRuntimeStats stats = GetOrCreateLocked(bucket, model);
                lock (stats.RateLimits)
                {
                    foreach (KeyValuePair<string, string> kv in found) stats.RateLimits[kv.Key] = kv.Value;
                }
                stats.RateLimitsAtLocal = DateTime.Now;

                // Limites de Groq/Cerebras sao por organizacao/modelo; o restante diario vale para todas as funcoes do mesmo modelo.
                _dirty = true;
            }
        }

        public static void ReportHttpFailure(string bucket, string model, int statusCode, string errorBody)
        {
            ReportHttpFailure(bucket, model, statusCode, errorBody, null, 0);
        }

        public static void ReportHttpFailure(string bucket, string model, int statusCode, string errorBody, TimeSpan? retryAfter, double latencyMs)
        {
            if (string.IsNullOrEmpty(model)) return;
            string body = errorBody ?? "";
            TimeSpan? cooldown = ComputeCooldown(statusCode, body, retryAfter);
            string limitNote = ExtractProviderLimitNote(body);

            lock (Sync)
            {
                ModelRuntimeStats stats = GetOrCreateLocked(bucket, model);
                RollDayLocked(stats);
                stats.FailuresToday++;
                stats.FailuresAllTime++;
                if (statusCode == 429) stats.RateLimitedToday++;
                stats.LastErrorReason = Truncate("HTTP " + statusCode + ": " + SummarizeError(body), 220);
                stats.LastErrorLocal = DateTime.Now;
                stats.IsActiveNow = false;
                if (!string.IsNullOrEmpty(limitNote)) stats.ProviderLimitNote = limitNote;

                if (cooldown.HasValue && cooldown.Value > TimeSpan.Zero)
                {
                    DateTime until = DateTime.UtcNow.Add(cooldown.Value);
                    ApplyCooldownLocked(stats, until);

                    // Cotas Google sao por modelo/projeto: esgotar em uma funcao esgota em todas.
                    if (Array.IndexOf(GoogleBuckets, (bucket ?? "").ToLowerInvariant()) >= 0 && (statusCode == 429 || statusCode == 404))
                    {
                        foreach (KeyValuePair<string, ModelRuntimeStats> kv in Registry)
                        {
                            ModelRuntimeStats other = kv.Value;
                            if (other != stats && string.Equals(other.ModelId, model, StringComparison.OrdinalIgnoreCase) &&
                                Array.IndexOf(GoogleBuckets, (other.Bucket ?? "").ToLowerInvariant()) >= 0)
                            {
                                ApplyCooldownLocked(other, until);
                                if (string.IsNullOrEmpty(other.LastErrorReason)) other.LastErrorReason = stats.LastErrorReason;
                            }
                        }
                    }
                }

                AddEventLocked(bucket, model, false, statusCode, latencyMs, 0, 0, Truncate(SummarizeError(body), 160));
                _dirty = true;
            }
        }

        private static void ApplyCooldownLocked(ModelRuntimeStats s, DateTime untilUtc)
        {
            if (untilUtc > s.CooldownUntilUtc) s.CooldownUntilUtc = untilUtc;
            s.IsActiveNow = false;
        }

        /// <summary>Calcula o tempo de descanso real a partir do status, da mensagem do provedor e do Retry-After.</summary>
        public static TimeSpan? ComputeCooldown(int statusCode, string body, TimeSpan? retryAfter)
        {
            string b = (body ?? "").ToLowerInvariant();
            TimeSpan? parsed = ParseRetryDelay(body);
            if (!parsed.HasValue && retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero) parsed = retryAfter;

            if (statusCode == 404 || b.Contains("model_not_found") || b.Contains("does not exist") || b.Contains("no longer available") || b.Contains("decommissioned"))
            {
                return TimeSpan.FromHours(12);
            }
            if (statusCode == 401 || statusCode == 403 || b.Contains("invalid api key") || b.Contains("api_key_invalid") || b.Contains("permission_denied"))
            {
                return TimeSpan.FromMinutes(30);
            }
            if (statusCode == 413)
            {
                return null; // pedido grande demais: nao e falta de cota, o proximo pedido menor funciona
            }
            if (statusCode == 429 || b.Contains("too_many_requests") || b.Contains("resource_exhausted") || b.Contains("exceeded your current quota") || b.Contains("rate limit"))
            {
                bool daily = b.Contains("per day") || b.Contains("perday") || b.Contains("per_day") || b.Contains("requests per day") ||
                             b.Contains("(rpd)") || b.Contains("tokens per day") || b.Contains("(tpd)") || b.Contains("limit: 0") ||
                             b.Contains("exceeded your current quota");
                if (parsed.HasValue)
                {
                    TimeSpan p = parsed.Value + TimeSpan.FromSeconds(1);
                    return p < TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : p;
                }
                if (daily) return UntilNextPacificMidnight();
                return TimeSpan.FromSeconds(60);
            }
            if (statusCode >= 500 || b.Contains("service_unavailable") || b.Contains("high demand") || b.Contains("overloaded"))
            {
                return TimeSpan.FromSeconds(20);
            }
            return null;
        }

        private static TimeSpan UntilNextPacificMidnight()
        {
            try
            {
                TimeZoneInfo pt = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
                DateTime nowPt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pt);
                DateTime nextMidnightPt = nowPt.Date.AddDays(1);
                DateTime nextUtc = TimeZoneInfo.ConvertTimeToUtc(nextMidnightPt, pt);
                TimeSpan d = nextUtc - DateTime.UtcNow;
                if (d > TimeSpan.Zero) return d;
            }
            catch { }
            return TimeSpan.FromHours(4);
        }

        public static TimeSpan? ParseRetryDelay(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                Match m = Regex.Match(body, "\"retryDelay\"\\s*:\\s*\"([\\d.]+)s\"", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    return TimeSpan.FromSeconds(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                }
                m = Regex.Match(body, @"(?:retry|try again)\s+(?:in|after)\s+((?:\d+h)?(?:\d+m(?!s))?(?:[\d.]+s)?(?:[\d.]+ms)?)", RegexOptions.IgnoreCase);
                if (m.Success && m.Groups[1].Value.Length > 0)
                {
                    string t = m.Groups[1].Value;
                    double total = 0;
                    Match h = Regex.Match(t, @"(\d+)h");
                    Match mi = Regex.Match(t, @"(\d+)m(?!s)");
                    Match se = Regex.Match(t, @"([\d.]+)s");
                    Match ms = Regex.Match(t, @"([\d.]+)ms");
                    if (h.Success) total += int.Parse(h.Groups[1].Value) * 3600.0;
                    if (mi.Success) total += int.Parse(mi.Groups[1].Value) * 60.0;
                    if (ms.Success) total += double.Parse(ms.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0;
                    else if (se.Success) total += double.Parse(se.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (total > 0) return TimeSpan.FromSeconds(total);
                }
            }
            catch { }
            return null;
        }

        private static string ExtractProviderLimitNote(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            Match m = Regex.Match(body, @"limit:\s*(\d+)\s+requests?\s+per\s+(day|minute|hour)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string per = m.Groups[2].Value.ToLowerInvariant() == "day" ? "dia" : (m.Groups[2].Value.ToLowerInvariant() == "minute" ? "min" : "h");
                return "Limite do provedor: " + m.Groups[1].Value + " req/" + per;
            }
            m = Regex.Match(body, @"on\s+(tokens|requests|input tokens)\s+per\s+(minute|day)\s+\((\w+)\):\s*Limit\s+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return "Limite do provedor: " + m.Groups[4].Value + " " + m.Groups[1].Value + "/" + (m.Groups[2].Value.ToLowerInvariant() == "day" ? "dia" : "min") + " (" + m.Groups[3].Value + ")";
            }
            Match qv = Regex.Match(body, "\"quotaValue\"\\s*:\\s*\"(\\d+)\"");
            Match qid = Regex.Match(body, "\"quotaId\"\\s*:\\s*\"([^\"]+)\"");
            if (qv.Success)
            {
                string id = qid.Success ? qid.Groups[1].Value : "";
                string per = id.IndexOf("PerDay", StringComparison.OrdinalIgnoreCase) >= 0 ? "/dia" : (id.IndexOf("PerMinute", StringComparison.OrdinalIgnoreCase) >= 0 ? "/min" : "");
                string what = id.IndexOf("Token", StringComparison.OrdinalIgnoreCase) >= 0 ? " tokens" : " req";
                return "Limite do provedor: " + qv.Groups[1].Value + what + per + (id.IndexOf("FreeTier", StringComparison.OrdinalIgnoreCase) >= 0 ? " (Free Tier)" : "");
            }
            return "";
        }

        private static string SummarizeError(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            Match m = Regex.Match(body, "\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            string msg = m.Success ? Regex.Unescape(m.Groups[1].Value) : body;
            return Regex.Replace(msg, @"\s+", " ").Trim();
        }

        /// <summary>Extrai tokens de entrada/saida de respostas OpenAI (usage), Gemini (usageMetadata) e Interactions (usage).</summary>
        public static void ExtractTokenUsage(string body, out long tokIn, out long tokOut)
        {
            tokIn = 0;
            tokOut = 0;
            if (string.IsNullOrEmpty(body)) return;
            string scan = body.Length <= 262144 ? body : (body.Substring(0, 16384) + body.Substring(body.Length - 16384));
            tokIn = FirstNumber(scan, "prompt_tokens|input_tokens|total_input_tokens|promptTokenCount");
            tokOut = FirstNumber(scan, "completion_tokens|output_tokens|total_output_tokens|candidatesTokenCount")
                   + FirstNumber(scan, "thoughtsTokenCount|total_thought_tokens");
        }

        private static long FirstNumber(string text, string keysAlternation)
        {
            Match m = Regex.Match(text, "\"(?:" + keysAlternation + ")\"\\s*:\\s*(\\d+)");
            long v;
            if (m.Success && long.TryParse(m.Groups[1].Value, out v)) return v;
            return 0;
        }

        private static void AddEventLocked(string bucket, string model, bool ok, int code, double latency, long tIn, long tOut, string note)
        {
            UsageEvent ev = new UsageEvent();
            ev.at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            ev.bucket = bucket ?? "";
            ev.model = model ?? "";
            ev.ok = ok;
            ev.code = code;
            ev.latency_ms = Math.Round(latency);
            ev.tok_in = tIn;
            ev.tok_out = tOut;
            ev.note = note ?? "";
            RecentEvents.Add(ev);
            while (RecentEvents.Count > MaxEvents) RecentEvents.RemoveAt(0);
        }

        // ------------------------------------------------------------------
        // Consultas para os paineis
        // ------------------------------------------------------------------
        private static bool HasKeyForBucket(string bucket)
        {
            FridaSettings s = _keySource;
            if (s == null) return true;
            string b = (bucket ?? "").ToLowerInvariant();
            if (b.Contains("groq")) return !string.IsNullOrEmpty((s.groq_api_key ?? "").Trim());
            if (b.Contains("cerebras")) return !string.IsNullOrEmpty((s.cerebras_api_key ?? "").Trim());
            if (b.Contains("zai")) return !string.IsNullOrEmpty((s.zai_glm_api_key ?? "").Trim());
            if (b.Contains("siliconflow") || b.EndsWith("_sf")) return !string.IsNullOrEmpty((s.siliconflow_api_key ?? "").Trim());
            if (b.Contains("openrouter")) return !string.IsNullOrEmpty((s.openrouter_api_key ?? "").Trim());
            return !string.IsNullOrEmpty((s.api_key ?? "").Trim());
        }

        /// <summary>Modelos com chave configurada, ordenados por funcao, provedor e preferencia.</summary>
        public static List<ModelRuntimeStats> GetAllStats()
        {
            List<ModelRuntimeStats> list = new List<ModelRuntimeStats>();
            lock (Sync)
            {
                foreach (KeyValuePair<string, ModelRuntimeStats> kvp in Registry)
                {
                    ModelRuntimeStats s = kvp.Value;
                    RollDayLocked(s);
                    if (!HasKeyForBucket(s.Bucket)) continue;
                    list.Add(s);
                }
            }
            return list
                .OrderBy(delegate(ModelRuntimeStats s) { return FunctionOrder(s.Bucket); })
                .ThenBy(delegate(ModelRuntimeStats s) { return GetPreferenceRank(s.Bucket, s.ModelId); })
                .ThenBy(delegate(ModelRuntimeStats s) { return s.ProviderLabel; })
                .ThenBy(delegate(ModelRuntimeStats s) { return s.ModelId; })
                .ToList();
        }

        private static int FunctionOrder(string bucket)
        {
            string b = (bucket ?? "").ToLowerInvariant();
            if (b.StartsWith("oai_")) return 0;
            if (b == "gemini") return 1;
            if (b.StartsWith("tts")) return 2;
            if (b.StartsWith("stt")) return 3;
            if (b == "gsearch") return 4;
            return 5;
        }

        public static List<UsageEvent> GetRecentEvents(int max)
        {
            lock (Sync)
            {
                int start = Math.Max(0, RecentEvents.Count - max);
                List<UsageEvent> list = RecentEvents.GetRange(start, RecentEvents.Count - start);
                list.Reverse();
                return list;
            }
        }

        public static void ForceClearCooldown(string bucket, string model)
        {
            if (string.IsNullOrEmpty(model)) return;
            string key = (bucket ?? "default") + ":" + model;
            lock (Sync)
            {
                ModelRuntimeStats stats;
                if (Registry.TryGetValue(key, out stats))
                {
                    stats.CooldownUntilUtc = DateTime.MinValue;
                }
                _dirty = true;
            }
        }

        public static void ClearAllCooldowns()
        {
            lock (Sync)
            {
                foreach (KeyValuePair<string, ModelRuntimeStats> kvp in Registry)
                {
                    kvp.Value.CooldownUntilUtc = DateTime.MinValue;
                }
                _dirty = true;
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
