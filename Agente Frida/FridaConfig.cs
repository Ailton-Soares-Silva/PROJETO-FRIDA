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
[assembly: AssemblyVersion("1.2.1.0")]

namespace FridaAgent
{
    // Selecao essencial: IDs fixos impedem a reentrada de modelos redundantes.
    public static class ModelSelection
    {
        public const string GroqBrain = "openai/gpt-oss-120b";
        public const string CerebrasBrain = "gpt-oss-120b";
        public const string GeminiComplex = "gemini-3.8-flash";
        public const string GeminiLite = "gemini-3.1-flash-lite";
        public const string Transcription = "whisper-large-v3-turbo";
        public const string Speech = "gemini-3.8-flash-lite-tts";
        // Francisca: ~2x mais rapida que a Thalita para sintetizar (563 caracteres: 10,3 s x 19,3 s em 03/11).
        public const string EdgeSpeech = "pt-BR-FranciscaNeural"; // Edge-TTS gratuito (bucket "tts_edge")
        public const string EdgeSpeechName = "Francisca";

        public static string[] BrainModels()
        {
            return new string[] { GeminiComplex, GeminiLite };
        }

        public static bool IsAllowed(string bucket, string model)
        {
            string b = (bucket ?? "").ToLowerInvariant();
            if (b == "oai_groq") return string.Equals(model, GroqBrain, StringComparison.OrdinalIgnoreCase);
            if (b == "oai_cerebras") return string.Equals(model, CerebrasBrain, StringComparison.OrdinalIgnoreCase);
            if (b == "stt_groq") return string.Equals(model, Transcription, StringComparison.OrdinalIgnoreCase);
            if (b == "tts") return string.Equals(model, Speech, StringComparison.OrdinalIgnoreCase);
            if (b == "tts_edge") return string.Equals(model, EdgeSpeech, StringComparison.OrdinalIgnoreCase);
            if (b == "stt") return string.Equals(model, GeminiLite, StringComparison.OrdinalIgnoreCase);
            if (b == "gemini" || b == "vision" || b == "gsearch")
                return string.Equals(model, GeminiComplex, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(model, GeminiLite, StringComparison.OrdinalIgnoreCase);
            return false;
        }
    }

    public class FridaSettings
    {
        // Provedores essenciais: Groq principal e Cerebras reserva.
        public string groq_api_key = "";
        public string zai_glm_api_key = "";     // Credencial legada preservada, sem rotas ativas.
        public string siliconflow_api_key = ""; // Credencial legada preservada, sem rotas ativas.
        public string cerebras_api_key = "";
        public string openrouter_api_key = "";  // Credencial legada preservada, sem rotas ativas.

        // Google Gemini key (dedicated to Frida's Revision 1 natural female voice Aoede + Vision/Backup)
        public string api_key = "";
        public bool prefer_zero_quota_tts = true; // true = Edge Neural Francisca (gratuita) como voz principal; Gemini Aoede (paga) so como reserva
        public string operator_name = "Ailton";
        public string whatsapp_python = "";
        public string whatsapp_profile = "";
        public string whatsapp_chrome = "";
        public string whatsapp_cache = "";
        public string whatsapp_chromedriver = "";
        public string whatsapp_routes_file = "whatsapp_routes.local.json";
        public string female_voice = "Aoede"; // Aoede, Kore, Leda, Zephyr
        public string tts_model = ModelSelection.Speech;
        public string transcribe_model = ModelSelection.GeminiLite;
        public string[] brain_models = ModelSelection.BrainModels();
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
        public bool play_startup_greeting = false;
        public int followup_listen_seconds = FridaSessionControl.IdleSeconds;
        public int wake_request_seconds = FridaSessionControl.IdleSeconds;
        public int preferred_mic_device = -1; // -1 = Auto-detect active physical mic (ignores dead Steam Streaming Mic)
        public int orb_x = -1;
        public int orb_y = -1;
        public double vad_speech_multiplier = 1.45;
        public int min_rms_threshold = 140;
        // Gerenciador de LLMs: chaves no formato "bucket:modelo" (ex: "oai_groq:openai/gpt-oss-120b")
        public string[] disabled_models = new string[0];
        public string[] preferred_models = new string[0];
        // Filtro "ouvir apenas o operador": exige perfil treinado (frida_speaker_profile.json)
        public bool speaker_lock_enabled = true;
        // true = nunca usar vozes robóticas de reserva (Google/Maria); sem cota Gemini a resposta aparece na tela
        public bool natural_voice_only = true;
        public double speaker_lock_tolerance = 1.0; // 0.75..1; valores menores tornam o filtro mais rigoroso
        // Limites definidos pelo operador para o painel Zelda quando o provedor nao informa (ex.: Gemini).
        // Chave "bucket:modelo:janela" com janela rpm|rph|rpd|tpm|tph|tpd. Ex.: "gemini:gemini-3.1-flash-lite:rpd": 300
        // Limites enviados pela API (Groq/Cerebras) sempre prevalecem sobre estes.
        public Dictionary<string, long> model_limits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

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

            // Normaliza configuracoes antigas sem reinserir aliases e previews.
            cfg.brain_models = ModelSelection.BrainModels();
            cfg.transcribe_model = ModelSelection.GeminiLite;
            cfg.tts_model = ModelSelection.Speech;
            cfg.play_startup_greeting = false;
            cfg.followup_listen_seconds = FridaSessionControl.IdleSeconds;
            cfg.wake_request_seconds = FridaSessionControl.IdleSeconds;
            if (cfg.disabled_models == null) cfg.disabled_models = new string[0];
            if (cfg.preferred_models == null) cfg.preferred_models = new string[0];
            cfg.model_limits = cfg.model_limits == null
                ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, long>(cfg.model_limits, StringComparer.OrdinalIgnoreCase);

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
            var result=FridaVerifiedTools.WriteFile(_filePath,new JavaScriptSerializer().Serialize(_data),false);
            if(result.Status!=FridaResultStatus.Verified)throw new IOException(result.Summary);
        }

        public string ExecuteAction(string action,string category,string content,string dueIso,string itemId)
        { return ExecuteVerifiedAction(action,category,content,dueIso,itemId).Output; }

        public FridaToolReceipt ExecuteVerifiedAction(string action,string category,string content,string dueIso,string itemId)
        {
            lock(_sync)
            {
                var json=new JavaScriptSerializer {MaxJsonLength=16*1024*1024};
                string before=json.Serialize(_data);string act=(action ?? "list").Trim().ToLowerInvariant();
                var receipt=new FridaToolReceipt("secretary_memory",act,_filePath,act!="list");
                try
                {
                    if(act=="list")
                    {
                        receipt.Check("memory_snapshot",true,"Estado em memoria",_data.items.Count+" itens","local_store_snapshot");
                        return receipt.Finish(FridaResultStatus.Verified,"memory_snapshot","Memoria consultada: "+_data.items.Count+" itens.",json.Serialize(_data));
                    }
                    SecretaryItem item=null;
                    if(act=="add" || act=="save" || act=="create")
                    {
                        if(string.IsNullOrWhiteSpace(content))return receipt.Fail("missing_content","Informe o conteudo do item.");
                        item=new SecretaryItem {id=Guid.NewGuid().ToString("N").Substring(0,12),category=string.IsNullOrWhiteSpace(category)?"nota":category,content=content,created_at=DateTime.Now.ToString("yyyy-MM-dd HH:mm"),due_iso=dueIso ?? ""};
                        _data.items.Add(item);
                    }
                    else if(act=="complete" || act=="done" || act=="delete")
                    {
                        var matches=_data.items.FindAll(x=>!string.IsNullOrEmpty(itemId)?string.Equals(x.id,itemId,StringComparison.OrdinalIgnoreCase):!string.IsNullOrWhiteSpace(content) && x.content.IndexOf(content,StringComparison.OrdinalIgnoreCase)>=0);
                        if(matches.Count!=1)return receipt.Fail("item_not_unique","Item ausente ou ambiguo. Informe um ID unico.");
                        item=matches[0];if(act=="delete")_data.items.Remove(item);else item.completed=true;
                    }
                    else return receipt.Fail("unknown_action","Acao de memoria desconhecida.");
                    var saved=FridaVerifiedTools.WriteFile(_filePath,json.Serialize(_data),false);
                    if(saved.Status!=FridaResultStatus.Verified){_data=json.Deserialize<SecretaryData>(before);return receipt.Fail("save_failed",saved.Summary);}
                    var reloaded=json.Deserialize<SecretaryData>(File.ReadAllText(_filePath,Encoding.UTF8));
                    bool matchesReload=json.Serialize(reloaded)==json.Serialize(_data);
                    receipt.Check("persisted_memory_roundtrip",matchesReload,FridaVerifiedTools.Sha256(Encoding.UTF8.GetBytes(json.Serialize(_data))),FridaVerifiedTools.Sha256(Encoding.UTF8.GetBytes(json.Serialize(reloaded))),"json_readback");
                    if(!matchesReload){_data=json.Deserialize<SecretaryData>(before);return receipt.Fail("readback_mismatch","A memoria persistida difere do estado solicitado.");}
                    receipt.Put("item_id",item.id);receipt.Put("sha256",FridaVerifiedTools.FileSha256(_filePath));
                    receipt.MonitorKind="file";receipt.MonitorHash=FridaVerifiedTools.FileSha256(_filePath);
                    return receipt.Finish(FridaResultStatus.Verified,"persisted_memory_state","Item da memoria salvo e conferido por releitura. ID "+item.id+".","ID: "+item.id);
                }
                catch(Exception ex){_data=json.Deserialize<SecretaryData>(before);return receipt.Fail(ex.GetType().Name,"Falha na memoria: "+ex.Message);}
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
