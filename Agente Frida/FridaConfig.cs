// language: C# 5 (.NET Framework 4.8), file: FridaConfig.cs, target: Windows 10/11 x64
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

[assembly: AssemblyTitle("Agente Frida - Assistente Holografica Autonoma")]
[assembly: AssemblyDescription("Agente Pessoal de Voz, Engenharia, Codigo, 3D e Automacao Total")]
[assembly: AssemblyProduct("Projeto Frida")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace FridaAgent
{
    public class FridaSettings
    {
        // High-speed Free-Tier Alternative & Chinese AI Providers (OpenAI-compatible)
        public string groq_api_key = "";         // Groq LPU: Whisper-v3-Turbo STT (2,000 req/day) + Qwen3 / Llama-3.3-70B (400-1,000 tok/s)
        public string zai_glm_api_key = "";      // Zhipu / Z.ai Chinese Official: GLM-4.5-Flash & GLM-4V-Flash (100% Permanent Free)
        public string siliconflow_api_key = "";  // SiliconFlow Chinese Cloud: Qwen3-8B / DeepSeek-R1 / SenseVoiceSmall ASR (Free)
        public string cerebras_api_key = "";     // Cerebras WSE: Qwen-3 32B / Llama 3.3 at 2,000+ tok/s (1M free tokens/day)
        public string openrouter_api_key = "";   // OpenRouter: DeepSeek-V3:free / Qwen3-235B:free / GLM-4.5-Air:free

        // Google Gemini key (dedicated to Frida's Revision 1 natural female voice Aoede + Vision/Backup)
        public string api_key = "";
        public bool prefer_zero_quota_tts = false; // false = Voz natural feminina Aoede da Revisao 1 como padrao
        public string operator_name = "Ailton";
        public string female_voice = "Aoede"; // Aoede, Kore, Leda, Zephyr
        public string tts_model = "gemini-3.8-flash-tts";
        public string transcribe_model = "gemini-flash-lite-latest";
        public string[] brain_models = new string[]
        {
            "gemini-flash-lite-latest",
            "gemini-3.5-flash-lite",
            "gemini-3-flash-preview",
            "gemma-4-26b-a4b-it",
            "gemini-3.5-flash",
            "gemini-3.8-flash",
            "gemini-3.1-flash-lite"
        };
        public string[] wake_phrases = new string[]
        {
            "Frida",
            "Oi Frida",
            "Ei Frida",
            "Ola Frida",
            "Ok Frida",
            "Fala Frida",
            "Ativar Frida",
            "Escuta Frida"
        };
        public bool start_with_windows = true;
        public bool play_startup_greeting = true;
        public int followup_listen_seconds = 7;
        public int preferred_mic_device = -1; // -1 = Auto-detect active physical mic (ignores dead Steam Streaming Mic)
        public int orb_x = -1;
        public int orb_y = -1;
        public double vad_speech_multiplier = 1.65;
        public int min_rms_threshold = 220;
        // Gerenciador de LLMs: chaves no formato "bucket:modelo" (ex: "oai_groq:openai/gpt-oss-120b")
        public string[] disabled_models = new string[0];
        public string[] preferred_models = new string[0];
        // Filtro "ouvir apenas o operador": exige perfil treinado (frida_voice_profile.json)
        public bool speaker_lock_enabled = true;
        // true = nunca usar vozes robóticas de reserva (Google/Maria); sem cota Gemini a resposta aparece na tela
        public bool natural_voice_only = true;
        public double speaker_lock_tolerance = 1.0; // >1 aceita mais variacao (ex: voz rouca), <1 mais rigoroso

        public static FridaSettings LoadOrCreate(string configPath)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            FridaSettings cfg = null;

            if (File.Exists(configPath))
            {
                try
                {
                    string raw = File.ReadAllText(configPath, Encoding.UTF8);
                    cfg = ser.Deserialize<FridaSettings>(raw);
                }
                catch { }
            }

            if (cfg == null)
            {
                cfg = new FridaSettings();
            }

            // Inherit API keys automatically from parent workspace config.json or environment if empty
            if (string.IsNullOrEmpty((cfg.api_key ?? "").Trim()))
            {
                cfg.api_key = TryDiscoverApiKey(Path.GetDirectoryName(configPath));
            }
            if (string.IsNullOrEmpty((cfg.groq_api_key ?? "").Trim()))
            {
                cfg.groq_api_key = (Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "").Trim();
            }
            if (string.IsNullOrEmpty((cfg.zai_glm_api_key ?? "").Trim()))
            {
                cfg.zai_glm_api_key = (Environment.GetEnvironmentVariable("ZAI_API_KEY") ?? Environment.GetEnvironmentVariable("ZHIPU_API_KEY") ?? "").Trim();
            }
            if (string.IsNullOrEmpty((cfg.siliconflow_api_key ?? "").Trim()))
            {
                cfg.siliconflow_api_key = (Environment.GetEnvironmentVariable("SILICONFLOW_API_KEY") ?? "").Trim();
            }
            if (string.IsNullOrEmpty((cfg.cerebras_api_key ?? "").Trim()))
            {
                cfg.cerebras_api_key = (Environment.GetEnvironmentVariable("CEREBRAS_API_KEY") ?? "").Trim();
            }
            if (string.IsNullOrEmpty((cfg.openrouter_api_key ?? "").Trim()))
            {
                cfg.openrouter_api_key = (Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "").Trim();
            }

            // Ensure high-quota Free-Tier resilient models lead the brain cascade even on older config.json files
            string[] resilientPriority = new string[]
            {
                "gemini-flash-lite-latest",
                "gemini-3.5-flash-lite",
                "gemini-3-flash-preview",
                "gemma-4-26b-a4b-it",
                "gemini-3.5-flash",
                "gemini-3.8-flash",
                "gemini-3.1-flash-lite"
            };
            List<string> mergedModels = new List<string>();
            for (int i = 0; i < resilientPriority.Length; i++)
            {
                mergedModels.Add(resilientPriority[i]);
            }
            if (cfg.brain_models != null)
            {
                for (int i = 0; i < cfg.brain_models.Length; i++)
                {
                    string existing = (cfg.brain_models[i] ?? "").Trim();
                    if (!string.IsNullOrEmpty(existing) && !mergedModels.Contains(existing))
                    {
                        mergedModels.Add(existing);
                    }
                }
            }
            cfg.brain_models = mergedModels.ToArray();
            if (cfg.disabled_models == null) cfg.disabled_models = new string[0];
            if (cfg.preferred_models == null) cfg.preferred_models = new string[0];

            Save(cfg, configPath);
            return cfg;
        }

        public bool HasAnyActiveProviderKey()
        {
            return !string.IsNullOrEmpty((groq_api_key ?? "").Trim())
                || !string.IsNullOrEmpty((zai_glm_api_key ?? "").Trim())
                || !string.IsNullOrEmpty((siliconflow_api_key ?? "").Trim())
                || !string.IsNullOrEmpty((cerebras_api_key ?? "").Trim())
                || !string.IsNullOrEmpty((openrouter_api_key ?? "").Trim())
                || !string.IsNullOrEmpty((api_key ?? "").Trim());
        }

        private static string TryDiscoverApiKey(string baseDir)
        {
            try
            {
                string envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                             ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
                if (!string.IsNullOrEmpty(envKey)) return envKey.Trim();

                JavaScriptSerializer ser = new JavaScriptSerializer();
                string dir = baseDir;
                for (int depth = 0; depth < 4 && !string.IsNullOrEmpty(dir); depth++)
                {
                    string candidate = Path.Combine(dir, "config.json");
                    if (File.Exists(candidate))
                    {
                        Dictionary<string, object> dict = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(candidate, Encoding.UTF8));
                        if (dict != null && dict.ContainsKey("api_key") && dict["api_key"] != null)
                        {
                            string k = dict["api_key"].ToString().Trim();
                            if (k.Length > 10) return k;
                        }
                    }
                    DirectoryInfo parent = Directory.GetParent(dir);
                    dir = parent != null ? parent.FullName : null;
                }
            }
            catch { }
            return "";
        }

        public static void Save(FridaSettings cfg, string configPath)
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string rawJson = ser.Serialize(cfg);
                string formatted = PrettyPrintJson(rawJson);
                File.WriteAllText(configPath, formatted, new UTF8Encoding(false));
            }
            catch { }
        }

        private static string PrettyPrintJson(string json)
        {
            StringBuilder sb = new StringBuilder();
            bool inQuotes = false;
            bool escaped = false;
            int indent = 0;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (escaped)
                {
                    sb.Append(c);
                    escaped = false;
                    continue;
                }
                if (c == '\\' && inQuotes)
                {
                    sb.Append(c);
                    escaped = true;
                    continue;
                }
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    sb.Append(c);
                    continue;
                }
                if (!inQuotes)
                {
                    if (c == '{' || c == '[')
                    {
                        sb.Append(c);
                        sb.Append(Environment.NewLine);
                        indent++;
                        sb.Append(new string(' ', indent * 2));
                        continue;
                    }
                    if (c == '}' || c == ']')
                    {
                        sb.Append(Environment.NewLine);
                        indent = Math.Max(0, indent - 1);
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(c);
                        continue;
                    }
                    if (c == ',')
                    {
                        sb.Append(c);
                        sb.Append(Environment.NewLine);
                        sb.Append(new string(' ', indent * 2));
                        continue;
                    }
                    if (c == ':')
                    {
                        sb.Append(": ");
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    public static class StartupRegistry
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "AgenteFrida";

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    object val = key.GetValue(ValueName);
                    return val != null && val.ToString().Length > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetAutoStart(bool enable, string exePath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        key.SetValue(ValueName, "\"" + exePath + "\"");
                    }
                    else
                    {
                        if (key.GetValue(ValueName) != null)
                        {
                            key.DeleteValue(ValueName, false);
                        }
                    }
                }
            }
            catch { }
        }
    }

    public class SecretaryItem
    {
        public string id = "";
        public string category = "nota"; // "nota", "tarefa", "lembrete"
        public string content = "";
        public string created_at = "";
        public string due_iso = "";
        public bool completed = false;
    }

    public class SecretaryData
    {
        public List<SecretaryItem> items = new List<SecretaryItem>();
    }

    public class SecretaryStore
    {
        private readonly string _filePath;
        private readonly object _sync = new object();
        private SecretaryData _data;

        public SecretaryStore(string filePath)
        {
            _filePath = filePath;
            Load();
        }

        private void Load()
        {
            lock (_sync)
            {
                if (File.Exists(_filePath))
                {
                    try
                    {
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        _data = ser.Deserialize<SecretaryData>(File.ReadAllText(_filePath, Encoding.UTF8));
                    }
                    catch { }
                }
                if (_data == null) _data = new SecretaryData();
                if (_data.items == null) _data.items = new List<SecretaryItem>();
            }
        }

        private void SaveLocked()
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(_filePath, ser.Serialize(_data), new UTF8Encoding(false));
            }
            catch { }
        }

        public string ExecuteAction(string action, string category, string content, string dueIso, string itemId)
        {
            lock (_sync)
            {
                string act = (action ?? "list").Trim().ToLowerInvariant();
                if (act == "add" || act == "save" || act == "create")
                {
                    SecretaryItem item = new SecretaryItem();
                    item.id = Guid.NewGuid().ToString("N").Substring(0, 6);
                    item.category = string.IsNullOrEmpty(category) ? "nota" : category.ToLowerInvariant();
                    item.content = content ?? "";
                    item.created_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    item.due_iso = dueIso ?? "";
                    item.completed = false;
                    _data.items.Add(item);
                    SaveLocked();
                    return string.Format("Registrado [{0}] #{1}: {2} {3}", item.category, item.id, item.content,
                        string.IsNullOrEmpty(item.due_iso) ? "" : "(Prazo: " + item.due_iso + ")");
                }
                else if (act == "complete" || act == "done" || act == "delete")
                {
                    for (int i = _data.items.Count - 1; i >= 0; i--)
                    {
                        if (_data.items[i].id.Equals(itemId ?? "", StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(content) && _data.items[i].content.IndexOf(content, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            string txt = _data.items[i].content;
                            if (act == "delete") _data.items.RemoveAt(i);
                            else _data.items[i].completed = true;
                            SaveLocked();
                            return "Item atualizado: " + txt;
                        }
                    }
                    return "Item nao localizado.";
                }
                else
                {
                    if (_data.items.Count == 0) return "Agenda e memoria da secretaria vazias no momento.";
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < _data.items.Count; i++)
                    {
                        SecretaryItem it = _data.items[i];
                        sb.AppendFormat("- [#{0} | {1} | {2}] {3}{4}\n",
                            it.id,
                            it.category,
                            it.completed ? "CONCLUIDO" : "ATIVO",
                            it.content,
                            string.IsNullOrEmpty(it.due_iso) ? "" : " (Agendado: " + it.due_iso + ")");
                    }
                    return sb.ToString().Trim();
                }
            }
        }

        public string BuildBriefContext()
        {
            lock (_sync)
            {
                if (_data.items.Count == 0) return "Sem tarefas ou notas pendentes.";
                StringBuilder sb = new StringBuilder();
                int count = 0;
                for (int i = _data.items.Count - 1; i >= 0 && count < 12; i--)
                {
                    SecretaryItem it = _data.items[i];
                    if (it.completed) continue;
                    sb.AppendFormat("[#{0} {1}] {2}{3}; ",
                        it.id, it.category, it.content,
                        string.IsNullOrEmpty(it.due_iso) ? "" : " @" + it.due_iso);
                    count++;
                }
                return sb.Length == 0 ? "Todas as tarefas concluidas." : sb.ToString();
            }
        }

        public List<SecretaryItem> PopDueReminders()
        {
            List<SecretaryItem> due = new List<SecretaryItem>();
            lock (_sync)
            {
                DateTime now = DateTime.Now;
                bool changed = false;
                for (int i = 0; i < _data.items.Count; i++)
                {
                    SecretaryItem it = _data.items[i];
                    if (!it.completed && !string.IsNullOrEmpty(it.due_iso))
                    {
                        DateTime dt;
                        if (DateTime.TryParse(it.due_iso, out dt) && dt <= now)
                        {
                            it.completed = true;
                            due.Add(it);
                            changed = true;
                        }
                    }
                }
                if (changed) SaveLocked();
            }
            return due;
        }
    }
}
