using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public sealed class FridaTaskStep
    {
        public int StepIndex { get; set; }
        public string Role { get; set; } // manager, assistant, technician, analyst, senior_researcher, secretary
        public string Instruction { get; set; }
        public string ToolHint { get; set; }

        public FridaTaskStep() { }

        public FridaTaskStep(int index, string role, string instruction, string toolHint = null)
        {
            StepIndex = index;
            Role = role ?? "technician";
            Instruction = instruction ?? "";
            ToolHint = toolHint ?? "";
        }
    }

    public sealed class FridaStructuredPlan
    {
        public string Objective { get; set; }
        public FridaTaskKind Kind { get; set; }
        public string Complexity { get; set; } // simple, moderate, complex
        public bool NeedsTools { get; set; }
        public bool NeedsWeb { get; set; }
        public bool NeedsLocalData { get; set; }
        public List<FridaTaskStep> Steps { get; set; }
        public List<string> CandidateSkills { get; set; }
        public string ClarificationQuestion { get; set; }
        public string AnswerDirectly { get; set; }
        public bool Valid { get; set; }
        public string Source { get; set; }

        public FridaStructuredPlan()
        {
            Objective = "";
            Kind = FridaTaskKind.Technical;
            Complexity = "moderate";
            Steps = new List<FridaTaskStep>();
            CandidateSkills = new List<string>();
            ClarificationQuestion = "";
            AnswerDirectly = "";
            Source = "roteamento local";
        }
    }

    public static class FridaPlanner
    {
        public const string SystemInstructions =
            "[MODO PLANEJADOR E TRIAGEM MULTI-IA]\n"
            + "Voce e o planejador de missao da FRIDA operando na bancada privada de Ailton Soares (dj) no Windows 11 com acesso total.\n"
            + "Ferramentas reais disponiveis no motor: open_program_or_url, inspect_screen, run_powershell, manage_files, fetch_url, web_search, web_research, read_whatsapp, memory, manage_skills, run_project_validation.\n"
            + "Sua missao: entender a INTENCAO REAL do operador, mesmo sem palavras-chave obvias ou usando linguagem coloquial (ex: 'to sem espaco no C:', 'o que mudou na norma de soldagem?', 'conferir se tem chassi atrasado').\n"
            + "Regras essenciais:\n"
            + "1. Se for conversa casual pura, saudacao ou explicacao teorica sem ferramentas/web: needs_tools=false, needs_web=false, needs_local_data=false e forneca a resposta em 'answer'.\n"
            + "2. Se envolver estado da maquina, arquivos, espaco, programas, automacao, planilhas, WhatsApp, normas ou dados: needs_tools=true, answer=\"\" e monte os 'steps' com papeis recomendados.\n"
            + "3. Se envolver fatos recentes, cotacoes, estoques, precos ou informacoes da web: needs_web=true e needs_tools=true.\n"
            + "4. Se envolver procedimento que a Frida deve aprender a executar sozinha: kind=skill_evolution, needs_tools=true.\n"
            + "5. Papeis recomendados nos steps:\n"
            + "   - 'assistant' (pesquisa web, coleta rapida)\n"
            + "   - 'technician' (execucao de comandos, PowerShell, codigo, arquivos)\n"
            + "   - 'senior_researcher' (cruzamento analitico, planilhas x WhatsApp, raciocinio profundo)\n"
            + "   - 'analyst' (auditoria, revisao de recibos e codigo)\n"
            + "   - 'secretary' (consolidacao da resposta final)\n"
            + "Responda ESTRITAMENTE em JSON com a estrutura:\n"
            + "{\n"
            + "  \"objective\": \"resumo claro do que o operador precisa\",\n"
            + "  \"kind\": \"conversation|simple_explanation|basic_open|research|deep_research|code_execution|application_build|technical_review|technical|skill_evolution\",\n"
            + "  \"complexity\": \"simple|moderate|complex\",\n"
            + "  \"requires_action\": true/false,\n"
            + "  \"needs_tools\": true/false,\n"
            + "  \"needs_web\": true/false,\n"
            + "  \"needs_local_data\": true/false,\n"
            + "  \"steps\": [\n"
            + "    {\"step_index\": 1, \"role\": \"technician|assistant|senior_researcher|analyst\", \"instruction\": \"o que fazer\", \"tool_hint\": \"ferramenta principal\"}\n"
            + "  ],\n"
            + "  \"candidate_skills\": [\"ids ou nomes de skills candidatas se houver\"],\n"
            + "  \"clarification_question\": \"pergunta somente se o pedido for ambiguo e impossivel de prosseguir\",\n"
            + "  \"answer\": \"resposta direta apenas para conversa/explicacao pura sem ferramentas; vazia caso contrario\"\n"
            + "}";

        public static string BuildPrompt(
            string request,
            IEnumerable<KeyValuePair<string, string>> history,
            string memorySummary = null,
            string skillsSummary = null)
        {
            var json = new JavaScriptSerializer();
            var turns = (history ?? Enumerable.Empty<KeyValuePair<string, string>>())
                .Reverse()
                .Take(FridaEconomyRouting.HistoryMessages)
                .Reverse()
                .Select(x => new KeyValuePair<string, string>(x.Key, FridaReliability.ContextText(x.Key, x.Value)))
                .Select(x => new { role = x.Key, text = x.Value.Length > FridaEconomyRouting.HistoryChars ? x.Value.Substring(0, FridaEconomyRouting.HistoryChars) : x.Value })
                .ToArray();

            var sb = new StringBuilder();
            sb.AppendLine(FridaInteractionPolicy.LocalDateContext() + FridaInteractionPolicy.UniversalUnrestrictedSystemPrompt);
            sb.AppendLine(SystemInstructions);

            if (!string.IsNullOrWhiteSpace(memorySummary))
            {
                sb.AppendLine("\n[MEMORIAS RELEVANTES DO OPERADOR E AMBIENTE]:");
                sb.AppendLine(memorySummary.Trim());
            }

            if (!string.IsNullOrWhiteSpace(skillsSummary))
            {
                sb.AppendLine("\n[CATALOGO DE SKILLS DISPONIVEIS]:");
                sb.AppendLine(skillsSummary.Trim());
            }

            sb.AppendLine("\n[CONTEXTO E PEDIDO ATUAL]:");
            sb.AppendLine(json.Serialize(new { history = turns, current_request = request }));

            return sb.ToString();
        }

        public static FridaStructuredPlan Parse(string response, FridaTaskPlan localPlan)
        {
            var plan = new FridaStructuredPlan
            {
                Kind = localPlan != null ? localPlan.Kind : FridaTaskKind.Technical,
                Complexity = (localPlan != null && localPlan.NeedsTeam) ? "complex" : "moderate",
                Source = "roteamento local"
            };

            if (string.IsNullOrWhiteSpace(response)) return plan;

            string text = response.Trim();
            try
            {
                if (!text.StartsWith("{"))
                {
                    int first = text.IndexOf('{');
                    int last = text.LastIndexOf('}');
                    if (first >= 0 && last > first) text = text.Substring(first, last - first + 1);
                }

                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                if (root == null || !root.ContainsKey("kind") || !root.ContainsKey("complexity")
                    || (!root.ContainsKey("requires_action") && !root.ContainsKey("needs_tools"))
                    || !root.ContainsKey("answer") || !(root["answer"] is string))
                    return plan;

                string c = Convert.ToString(root["complexity"]).ToLowerInvariant();
                if (c != "simple" && c != "moderate" && c != "complex")
                    return plan;
                plan.Complexity = c;

                bool actionNeeded = false;
                if (root.ContainsKey("requires_action") && root["requires_action"] is bool)
                    actionNeeded = (bool)root["requires_action"];
                else if (root.ContainsKey("needs_tools") && root["needs_tools"] is bool)
                    actionNeeded = (bool)root["needs_tools"];
                else
                    return plan;
                plan.NeedsTools = actionNeeded;

                if (root.ContainsKey("kind") && root["kind"] is string)
                {
                    string k = ((string)root["kind"]).ToLowerInvariant();
                    switch (k)
                    {
                        case "conversation": plan.Kind = FridaTaskKind.Conversation; break;
                        case "simple_explanation": plan.Kind = FridaTaskKind.Technical; break;
                        case "basic_open": plan.Kind = FridaTaskKind.BasicOpen; break;
                        case "research": plan.Kind = FridaTaskKind.Research; break;
                        case "deep_research": plan.Kind = FridaTaskKind.DeepResearch; break;
                        case "code_execution": plan.Kind = FridaTaskKind.CodeExecution; break;
                        case "application_build": plan.Kind = FridaTaskKind.ApplicationBuild; break;
                        case "technical_review": plan.Kind = FridaTaskKind.TechnicalReview; break;
                        case "technical": plan.Kind = FridaTaskKind.Technical; break;
                        case "skill_evolution": plan.Kind = FridaTaskKind.SkillEvolution; break;
                        default: return plan;
                    }
                }
                else return plan;

                if (root.ContainsKey("objective") && root["objective"] is string)
                    plan.Objective = ((string)root["objective"]).Trim();

                if (root.ContainsKey("needs_web") && root["needs_web"] is bool)
                    plan.NeedsWeb = (bool)root["needs_web"];

                if (root.ContainsKey("needs_local_data") && root["needs_local_data"] is bool)
                    plan.NeedsLocalData = (bool)root["needs_local_data"];

                if (root.ContainsKey("answer") && root["answer"] is string)
                    plan.AnswerDirectly = (string)root["answer"];

                if (root.ContainsKey("clarification_question") && root["clarification_question"] is string)
                    plan.ClarificationQuestion = (string)root["clarification_question"];

                if (root.ContainsKey("candidate_skills") && root["candidate_skills"] is IEnumerable)
                {
                    foreach (object item in (IEnumerable)root["candidate_skills"])
                    {
                        string s = item as string;
                        if (!string.IsNullOrWhiteSpace(s) && !plan.CandidateSkills.Contains(s))
                            plan.CandidateSkills.Add(s.Trim());
                    }
                }

                if (root.ContainsKey("steps") && root["steps"] is IEnumerable)
                {
                    int autoIndex = 1;
                    foreach (object item in (IEnumerable)root["steps"])
                    {
                        var stepDict = item as Dictionary<string, object>;
                        if (stepDict != null)
                        {
                            int idx = autoIndex;
                            if (stepDict.ContainsKey("step_index"))
                                int.TryParse(Convert.ToString(stepDict["step_index"]), out idx);

                            string role = stepDict.ContainsKey("role") ? Convert.ToString(stepDict["role"]) : "technician";
                            string instruction = stepDict.ContainsKey("instruction") ? Convert.ToString(stepDict["instruction"]) : "";
                            string hint = stepDict.ContainsKey("tool_hint") ? Convert.ToString(stepDict["tool_hint"]) : "";

                            if (!string.IsNullOrWhiteSpace(instruction))
                            {
                                plan.Steps.Add(new FridaTaskStep(idx, role, instruction, hint));
                                autoIndex++;
                            }
                        }
                    }
                }

                plan.Valid = true;
                plan.Source = ModelSelection.GeminiLite;
            }
            catch
            {
                // Resposta em texto puro em modo conversa
                if (localPlan != null && localPlan.Kind == FridaTaskKind.Conversation && !text.StartsWith("{") && !text.StartsWith("["))
                {
                    plan.AnswerDirectly = text;
                }
            }

            return plan;
        }
    }
}
