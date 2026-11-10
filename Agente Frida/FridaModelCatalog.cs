using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public sealed class FridaModelInfo
    {
        public string Id { get; set; }
        public string ProviderBucket { get; set; }
        public string ModelId { get; set; }
        public string DisplayName { get; set; }
        public List<string> Strengths { get; set; }
        public int ContextWindow { get; set; }
        public bool SupportsTools { get; set; }
        public string CostTier { get; set; } // free, low, medium, high
        public string EndpointUrl { get; set; }
        public List<string> FallbackIds { get; set; }

        public FridaModelInfo()
        {
            Strengths = new List<string>();
            FallbackIds = new List<string>();
            SupportsTools = true;
            CostTier = "free";
            ContextWindow = 128000;
        }

        public bool HasKey(FridaSettings settings)
        {
            if (settings == null) return false;
            if (ProviderBucket == "tts_edge") return true;
            if (ProviderBucket.Contains("cerebras")) return !string.IsNullOrWhiteSpace(settings.cerebras_api_key);
            if (ProviderBucket.Contains("groq")) return !string.IsNullOrWhiteSpace(settings.groq_api_key);
            if (ProviderBucket.Contains("kimi") || ProviderBucket.Contains("moonshot")) return !string.IsNullOrWhiteSpace(settings.kimi_api_key);
            if (ProviderBucket.Contains("deepseek")) return !string.IsNullOrWhiteSpace(settings.deepseek_api_key);
            if (ProviderBucket == "gemini") return !string.IsNullOrWhiteSpace(settings.api_key);
            return false;
        }

        public bool IsAvailable(FridaSettings settings)
        {
            return HasKey(settings) && ModelQuotaGuard.IsAvailable(ProviderBucket, ModelId);
        }
    }

    public static class FridaModelCatalog
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, FridaModelInfo> _models = new Dictionary<string, FridaModelInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<TeamRole, List<string>> _rolePreferences = new Dictionary<TeamRole, List<string>>();

        static FridaModelCatalog()
        {
            InitializeDefaultCatalog();
        }

        private static void InitializeDefaultCatalog()
        {
            lock (Sync)
            {
                _models.Clear();

                RegisterModel(new FridaModelInfo
                {
                    Id = "kimi-k3",
                    ProviderBucket = "oai_kimi",
                    ModelId = ModelSelection.KimiK3,
                    DisplayName = "Kimi K3 (Engenheiro)",
                    Strengths = new List<string> { "code", "tools", "refactor", "large_context" },
                    ContextWindow = 200000,
                    SupportsTools = true,
                    CostTier = "medium",
                    EndpointUrl = "https://api.moonshot.ai/v1/chat/completions",
                    FallbackIds = new List<string> { "deepseek-v4", "groq-brain", "gemini-complex" }
                });

                RegisterModel(new FridaModelInfo
                {
                    Id = "deepseek-v4",
                    ProviderBucket = "oai_deepseek",
                    ModelId = ModelSelection.DeepSeekProModel,
                    DisplayName = "DeepSeek V4 Pro (Gestor / Cérebro)",
                    Strengths = new List<string> { "reasoning", "planning", "code", "architecture" },
                    ContextWindow = 128000,
                    SupportsTools = true,
                    CostTier = "low",
                    EndpointUrl = "https://api.deepseek.com/chat/completions",
                    FallbackIds = new List<string> { "deepseek-r1", "gemini-complex", "groq-brain" }
                });

                RegisterModel(new FridaModelInfo
                {
                    Id = "deepseek-r1",
                    ProviderBucket = "oai_deepseek",
                    ModelId = ModelSelection.DeepSeekR1,
                    DisplayName = "DeepSeek R1 (Raciocínio Profundo / Analítico)",
                    Strengths = new List<string> { "deep_reasoning", "cross_analysis", "math", "investigation" },
                    ContextWindow = 64000,
                    SupportsTools = false,
                    CostTier = "medium",
                    EndpointUrl = "https://api.deepseek.com/chat/completions",
                    FallbackIds = new List<string> { "gemini-complex", "deepseek-v4" }
                });

                RegisterModel(new FridaModelInfo
                {
                    Id = "deepseek-chat",
                    ProviderBucket = "oai_deepseek",
                    ModelId = ModelSelection.DeepSeekModel,
                    DisplayName = "DeepSeek Chat (Assistente Geral)",
                    Strengths = new List<string> { "general", "conversation", "speed" },
                    ContextWindow = 128000,
                    SupportsTools = true,
                    CostTier = "low",
                    EndpointUrl = "https://api.deepseek.com/chat/completions",
                    FallbackIds = new List<string> { "groq-brain", "gemini-lite" }
                });

                RegisterModel(new FridaModelInfo
                {
                    Id = "cerebras-brain",
                    ProviderBucket = "oai_cerebras",
                    ModelId = ModelSelection.CerebrasBrain,
                    DisplayName = "Cerebras GPT OSS (Auditor Ultrarrápido)",
                    Strengths = new List<string> { "ultra_speed", "audit", "triage" },
                    ContextWindow = 128000,
                    SupportsTools = true,
                    CostTier = "free",
                    EndpointUrl = "https://api.cerebras.ai/v1/chat/completions",
                    FallbackIds = new List<string> { "groq-brain", "deepseek-v4", "gemini-lite" }
                });

                RegisterModel(new FridaModelInfo
                {
                    Id = "groq-brain",
                    ProviderBucket = "oai_groq",
                    ModelId = ModelSelection.GroqBrain,
                    DisplayName = "Groq GPT OSS (Velocidade e Ferramentas)",
                    Strengths = new List<string> { "speed", "tools", "triage", "draft" },
                    ContextWindow = 128000,
                    SupportsTools = true,
                    CostTier = "free",
                    EndpointUrl = "https://api.groq.com/openai/v1/chat/completions",
                    FallbackIds = new List<string> { "cerebras-brain", "gemini-lite" }
                });

                RegisterModel(new FridaModelInfo
                {
                    Id = "gemini-complex",
                    ProviderBucket = "gemini",
                    ModelId = ModelSelection.GeminiComplex,
                    DisplayName = "Gemini Complex (Visão, Web e Raciocínio Multimodal)",
                    Strengths = new List<string> { "vision", "web", "multimodal", "complex_code" },
                    ContextWindow = 1000000,
                    SupportsTools = true,
                    CostTier = "medium",
                    EndpointUrl = "https://generativelanguage.googleapis.com/v1beta/models/",
                    FallbackIds = new List<string> { "gemini-lite", "groq-brain" }
                });

                RegisterModel(new FridaModelInfo
                {
                    Id = "gemini-lite",
                    ProviderBucket = "gemini",
                    ModelId = ModelSelection.GeminiLite,
                    DisplayName = "Gemini Lite (Voz, Resumo e Resposta Rápida)",
                    Strengths = new List<string> { "speed", "speech_summary", "concise" },
                    ContextWindow = 1000000,
                    SupportsTools = true,
                    CostTier = "low",
                    EndpointUrl = "https://generativelanguage.googleapis.com/v1beta/models/",
                    FallbackIds = new List<string> { "groq-brain", "cerebras-brain" }
                });

                _rolePreferences.Clear();
                _rolePreferences[TeamRole.Technician] = new List<string> { "kimi-k3", "deepseek-v4", "groq-brain", "cerebras-brain", "gemini-complex" };
                _rolePreferences[TeamRole.Manager] = new List<string> { "deepseek-v4", "deepseek-r1", "gemini-complex", "groq-brain" };
                _rolePreferences[TeamRole.WhatsAppResearcher] = new List<string> { "deepseek-r1", "gemini-complex", "deepseek-v4" };
                _rolePreferences[TeamRole.Analyst] = new List<string> { "cerebras-brain", "groq-brain", "deepseek-v4", "gemini-lite" };
                _rolePreferences[TeamRole.Assistant] = new List<string> { "deepseek-chat", "groq-brain", "cerebras-brain", "gemini-lite" };
                _rolePreferences[TeamRole.Secretary] = new List<string> { "gemini-lite", "groq-brain", "cerebras-brain" };
            }
        }

        public static void RegisterModel(FridaModelInfo model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Id)) return;
            _models[model.Id] = model;
        }

        public static FridaModelInfo GetModel(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (Sync)
            {
                FridaModelInfo info;
                _models.TryGetValue(id, out info);
                return info;
            }
        }

        public static List<FridaModelInfo> GetAllModels()
        {
            lock (Sync) { return _models.Values.ToList(); }
        }

        public static FridaModelInfo ResolveModelForRole(TeamRole role, FridaSettings settings)
        {
            lock (Sync)
            {
                List<string> prefs;
                if (!_rolePreferences.TryGetValue(role, out prefs) || prefs == null) return null;

                for (int i = 0; i < prefs.Count; i++)
                {
                    FridaModelInfo m;
                    if (_models.TryGetValue(prefs[i], out m) && m.IsAvailable(settings))
                        return m;
                }
                return null;
            }
        }

        public static FridaModelInfo ResolveNextFallback(TeamRole role, string currentModelId, FridaSettings settings)
        {
            lock (Sync)
            {
                List<string> prefs;
                if (!_rolePreferences.TryGetValue(role, out prefs) || prefs == null) return null;

                bool foundCurrent = false;
                for (int i = 0; i < prefs.Count; i++)
                {
                    FridaModelInfo m;
                    if (!_models.TryGetValue(prefs[i], out m)) continue;

                    if (!foundCurrent)
                    {
                        if (string.Equals(m.ModelId, currentModelId, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(m.Id, currentModelId, StringComparison.OrdinalIgnoreCase))
                        {
                            foundCurrent = true;
                        }
                        continue;
                    }

                    if (m.IsAvailable(settings)) return m;
                }

                // If not found in subsequent list, check any available
                for (int i = 0; i < prefs.Count; i++)
                {
                    FridaModelInfo m;
                    if (_models.TryGetValue(prefs[i], out m) && m.IsAvailable(settings)
                        && !string.Equals(m.ModelId, currentModelId, StringComparison.OrdinalIgnoreCase))
                        return m;
                }
                return null;
            }
        }

        internal static List<OpenAiProviderRoute> OrderRoutesForRole(List<OpenAiProviderRoute> routes, string roleOrStrength, FridaSettings settings)
        {
            if (routes == null || routes.Count <= 1) return routes ?? new List<OpenAiProviderRoute>();

            TeamRole? targetRole = null;
            if (!string.IsNullOrWhiteSpace(roleOrStrength))
            {
                string r = roleOrStrength.Trim().ToLowerInvariant();
                if (r == "technician" || r == "engineer" || r == "engenheiro" || r == "tecnico" || r == "code") targetRole = TeamRole.Technician;
                else if (r == "manager" || r == "gestor" || r == "cerebro") targetRole = TeamRole.Manager;
                else if (r == "senior_researcher" || r == "whatsapp" || r == "cross" || r == "cruzamento") targetRole = TeamRole.WhatsAppResearcher;
                else if (r == "analyst" || r == "analista" || r == "auditor") targetRole = TeamRole.Analyst;
                else if (r == "assistant" || r == "estagiario" || r == "pesquisa") targetRole = TeamRole.Assistant;
                else if (r == "secretary" || r == "secretaria") targetRole = TeamRole.Secretary;
            }

            if (!targetRole.HasValue) targetRole = TeamRole.Technician; // default technical execution role

            lock (Sync)
            {
                List<string> prefs;
                if (!_rolePreferences.TryGetValue(targetRole.Value, out prefs) || prefs == null)
                    return routes;

                var result = new List<OpenAiProviderRoute>();
                var remaining = new List<OpenAiProviderRoute>(routes);

                foreach (string prefId in prefs)
                {
                    FridaModelInfo info;
                    if (!_models.TryGetValue(prefId, out info)) continue;

                    for (int i = 0; i < remaining.Count; i++)
                    {
                        var route = remaining[i];
                        if (string.Equals(route.ProviderBucket, info.ProviderBucket, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(route.ModelId, info.ModelId, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add(route);
                            remaining.RemoveAt(i);
                            break;
                        }
                    }
                }

                // Append any remaining routes
                result.AddRange(remaining);
                return result;
            }
        }
    }
}
