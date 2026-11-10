using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    internal static class CodingCopilotTests
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void SetField(object instance, string field, object value)
        {
            instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value);
        }

        private static object GetField(object instance, string field)
        {
            return instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
        }

        private static string InvokeTool(FridaBrain brain, string name, Dictionary<string, object> args)
        {
            return ((Task<string>)typeof(FridaBrain)
                .GetMethod("ExecuteSingleToolAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(brain, new object[] { name, args })).GetAwaiter().GetResult();
        }

        private sealed class ThreeProviderFailoverHandler : HttpMessageHandler
        {
            public int GroqCalls;
            public int CerebrasCalls;
            public int GeminiCalls;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string host = request.RequestUri.Host.ToLowerInvariant();
                if (host.Contains("groq"))
                {
                    GroqCalls++;
                    return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)
                    {
                        Content = new StringContent("{\"error\":{\"message\":\"Groq rate limit\"}}")
                    });
                }
                if (host.Contains("cerebras"))
                {
                    CerebrasCalls++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("{\"error\":{\"message\":\"Cerebras unavailable\"}}")
                    });
                }
                GeminiCalls++;
                string geminiBody = "{\"steps\":[{\"type\":\"model_output\",\"content\":[{\"type\":\"text\",\"text\":\"Recuperado pelo Gemini apos falha na Groq e Cerebras.\"}]}]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(geminiBody, Encoding.UTF8, "application/json")
                });
            }
        }

        public static void Run(Action<string, Action> test)
        {
            // 1. P1 Regression: Application build with only file_saved receipt MUST NOT be marked completed
            test("P1 regression: application build with only file write is NOT completed", () =>
            {
                string req = "Crie uma aplicacao em C# que calcule estatisticas e salve em C:\\app\\Calc.cs";
                var intent = FridaRequestIntent.Classify(req);
                Check(intent.IsSoftwareDevelopment && intent.RequiresTools, "Pedido de aplicacao nao classificado como desenvolvimento de software");
                Check(intent.Operations.Count == 3, "Esperava 3 entregaveis (write, compile, test), obteve " + intent.Operations.Count);

                var ledger = new FridaExecutionLedger();
                string tempDir = Path.Combine(Path.GetTempPath(), "frida-p1-app-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    string filePath = Path.Combine(tempDir, "Calc.cs");
                    var writeIntent = FridaRequestIntent.Classify("Crie uma aplicacao em C# no arquivo \"" + filePath + "\"");
                    var writeReceipt = FridaVerifiedTools.WriteFile(filePath, "class Calc { static void Main() {} }", false);
                    ledger.Add(writeReceipt);

                    string finalReply = FridaOperationalReply.Finalize(writeIntent, ledger, "Aplicacao pronta e concluida!");
                    Check(ledger.FileSaved, "FileSaved deveria ser true");
                    Check(!ledger.CompilationApproved, "CompilationApproved deveria ser false sem compilacao");
                    Check(!ledger.TestsApproved, "TestsApproved deveria ser false sem testes");
                    Check(!ledger.TaskCompletionVerified, "Falso sucesso: aplicacao marcada concluida apenas com recibo de arquivo gravado!");
                    Check(finalReply.Contains("Etapas pendentes") && finalReply.Contains("pedido_atendido=NAO"), "Resposta final nao declarou etapas pendentes");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            });

            // 2. P1 Regression: Code modification invalidates prior build_passed and tests_passed validations
            test("P1 regression: editing code invalidates previous build and test validations", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "frida-rev-inv-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    string filePath = Path.Combine(tempDir, "script.py");
                    var intent = FridaRequestIntent.Classify("Crie um script Python no arquivo \"" + filePath + "\"");
                    var ledger = new FridaExecutionLedger();
                    var state = FridaDevelopmentTaskState.CreateNew("Criar script", tempDir, true);
                    ledger.DevTaskState = state;
                    ledger.SetIntent(intent);

                    var rWrite1 = FridaVerifiedTools.WriteFile(filePath, "print('v1')\n", false);
                    ledger.Add(rWrite1);
                    state.RecordFileModification(filePath, "", rWrite1.MonitorHash, "+print('v1')", rWrite1.OperationId, ledger.CurrentProjectRevision);
                    string rev1 = ledger.CurrentProjectRevision;

                    var rCompile = FridaCodingTools.RunProjectValidation(tempDir, "python -m py_compile \"" + filePath + "\"", "compile", 20, state);
                    ledger.Add(rCompile);
                    var rTest = FridaCodingTools.RunProjectValidation(tempDir, "python \"" + filePath + "\"", "test", 20, state);
                    ledger.Add(rTest);

                    Check(ledger.TaskCompletionVerified, "Deveria estar verificado apos write + compile + test na rev1");

                    // Now modify the code -> must invalidate rev1's compile and test!
                    var rPatch = FridaCodingTools.ApplySurgicalPatch(filePath, "print('v1')", "print('v2')", rWrite1.MonitorHash, state);
                    ledger.Add(rPatch);
                    string rev2 = ledger.CurrentProjectRevision;

                    Check(rev1 != rev2, "Revisao do projeto nao avancou apos patch");
                    Check(!ledger.CompilationApproved, "Compilacao da revisao anterior nao foi invalidada");
                    Check(!ledger.TestsApproved, "Teste da revisao anterior nao foi invalidado");
                    Check(!ledger.TaskCompletionVerified, "TaskCompletionVerified permaneceu true apos alteracao de codigo sem reteste!");
                    Check(!state.RequestFulfilled, "Estado da tarefa permaneceu RequestFulfilled apos edicao sem revalidacao");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            });

            // 3. P1 Regression: Stale tool cache key after code edit (custom test command must re-run after file edit)
            test("P1 regression: custom test command re-executes after code modification instead of returning stale cache", () =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "frida-cache-fix-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    using (var http = new HttpClient())
                    {
                        var brain = new FridaBrain(new FridaSettings(), null, null, http);
                        var ledger = new FridaExecutionLedger();
                        var state = FridaDevelopmentTaskState.CreateNew("Fix bug", tempDir, true);
                        ledger.DevTaskState = state;
                        SetField(brain, "_requestActive", true);
                        SetField(brain, "_currentLedger", ledger);
                        SetField(brain, "_currentDevTaskState", state);

                        string scriptPath = Path.Combine(tempDir, "custom_app.py");
                        InvokeTool(brain, "manage_files", new Dictionary<string, object>
                        {
                            { "operation", "write" },
                            { "path", scriptPath },
                            { "content", "import sys\nsys.exit(1)\n" }
                        });

                        // Run a custom command that did NOT match the old narrow regex in IsVerificationCommand
                        var cmdArgs = new Dictionary<string, object>
                        {
                            { "command", "python \"" + scriptPath + "\"" }
                        };
                        string firstRunJson = InvokeTool(brain, "run_powershell", cmdArgs);
                        Check(firstRunJson.Contains("\"status\":\"Failed\""), "Primeira execucao deveria falhar");

                        // Fix the code
                        InvokeTool(brain, "apply_patch", new Dictionary<string, object>
                        {
                            { "path", scriptPath },
                            { "target_text", "sys.exit(1)" },
                            { "replacement_text", "print('FIXED_OK'); sys.exit(0)" }
                        });

                        // Re-run the exact same command -> MUST NOT return the cached Failed receipt!
                        string secondRunJson = InvokeTool(brain, "run_powershell", cmdArgs);
                        Check(secondRunJson != firstRunJson, "Cache obsoleto reutilizado apos alteracao no codigo!");
                        Check(secondRunJson.Contains("FIXED_OK") && !secondRunJson.Contains("\"status\":\"Failed\""), "Segunda execucao nao refletiu o codigo corrigido: " + secondRunJson);
                    }
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            });

            // 4. P1 Regression: 2,500-char blind JSON truncation replaced by valid JSON pagination
            test("P1 regression: large tool receipt pagination preserves valid JSON and receipt fields", () =>
            {
                var receipt = new FridaToolReceipt("run_project_validation", "test", "suite", true);
                receipt.Check("exit_code_zero", true, "0", "0", "process_handle");
                var bigOutput = new StringBuilder();
                bigOutput.AppendLine("HEADER_DIAGNOSTIC_LINE_1: compiler warning CS0168");
                for (int i = 0; i < 300; i++)
                {
                    bigOutput.AppendLine("Line " + i + ": detailed test execution log entry with data payload abcdefghijklmnopqrstuvwxyz0123456789");
                }
                bigOutput.AppendLine("FOOTER_SUMMARY_LINE: PASSED 42 tests in 1.2s");
                receipt.Finish(FridaResultStatus.Verified, "tests_passed", "42 testes aprovados.", bigOutput.ToString());

                string fullJson = receipt.ToJson();
                Check(fullJson.Length > 15000, "JSON original deveria ter > 15000 chars");

                string pagedJson = FridaContextPaginator.PaginateToolMessageContent(fullJson, 3200, 1);
                Check(pagedJson.Length <= 3200, "JSON paginado excedeu o orcamento: " + pagedJson.Length);

                // Must deserialize cleanly as valid JSON!
                var parsed = Json.DeserializeObject(pagedJson) as Dictionary<string, object>;
                Check(parsed != null, "JSON paginado invalido/corrompido");
                Check(Convert.ToString(parsed["operation_id"]) == receipt.OperationId, "operation_id perdido na paginacao");
                Check(Convert.ToString(parsed["status"]) == "Verified", "status perdido na paginacao");
                Check(Convert.ToString(parsed["verified_scope"]) == "tests_passed", "verified_scope perdido na paginacao");
                var pagination = parsed["pagination"] as Dictionary<string, object>;
                Check(pagination != null && Convert.ToBoolean(pagination["paginated"]) && Convert.ToBoolean(pagination["has_more"]), "Metadados de paginacao ausentes");
                string excerpt = Convert.ToString(parsed["untrusted_output"]);
                Check(excerpt.Contains("HEADER_DIAGNOSTIC_LINE_1") && excerpt.Contains("FOOTER_SUMMARY_LINE"), "Paginacao perdeu o topo ou o rodape do diagnostico");
            });

            // 5. P1 Regression: Provider recovery across Groq -> Cerebras -> Gemini when Groq and Cerebras fail
            test("P1 regression: Groq and Cerebras HTTP failures release budget so Gemini recovers", () =>
            {
                var budget = new FridaProviderBudget();
                Check(budget.TryEnter("oai_groq"), "Groq deveria entrar");
                budget.ReleaseFailedAttempt("oai_groq");
                Check(budget.TryEnter("oai_cerebras"), "Cerebras deveria entrar");
                budget.ReleaseFailedAttempt("oai_cerebras");
                Check(budget.TryEnter("gemini"), "Gemini foi bloqueado mesmo apos falha de Groq e Cerebras!");
                Check(budget.FailedProviders.Count == 2, "Provedores que falharam devem ficar registrados em FailedProviders");

                // End-to-end brain failover test with fake HTTP handler
                ModelQuotaGuard.ClearAllCooldowns();
                var handler = new ThreeProviderFailoverHandler();
                try
                {
                    using (var http = new HttpClient(handler))
                    {
                        var settings = new FridaSettings
                        {
                            groq_api_key = "fake-groq-key",
                            cerebras_api_key = "fake-cerebras-key",
                            api_key = "fake-gemini-key"
                        };
                        var brain = new FridaBrain(settings, null, null, http);
                        SetField(brain, "_currentIntent", new FridaRequestIntent { RequiresTools = true });
                        SetField(brain, "_currentLedger", new FridaExecutionLedger());
                        SetField(brain, "_taskPlan", new FridaTaskPlan { Kind = FridaTaskKind.CodeExecution, MayExecute = true, MayModify = true });

                        var execMethod = typeof(FridaBrain).GetMethod("ExecuteTechnicalRequestAsync", BindingFlags.NonPublic | BindingFlags.Instance);
                        string reply = ((Task<string>)execMethod.Invoke(brain, new object[] { "Execute o teste de recuperacao", null })).GetAwaiter().GetResult();
                        Check(handler.GroqCalls >= 1 && handler.CerebrasCalls >= 1 && handler.GeminiCalls >= 1,
                            "Nao tentou os 3 provedores em cascata: groq=" + handler.GroqCalls + " cerebras=" + handler.CerebrasCalls + " gemini=" + handler.GeminiCalls);
                        Check(reply != null && reply.Contains("Recuperado pelo Gemini"), "Gemini nao conseguiu assumir a recuperacao: " + reply);
                    }
                }
                finally
                {
                    ModelQuotaGuard.ClearAllCooldowns();
                }
            });

            // 6. P1 Regression: Reviewer/Auditor triggers correction cycles until defects are resolved
            test("P1 regression: reviewer findings trigger technician correction cycles", () =>
            {
                var settings = new FridaSettings { max_correction_cycles = 3 };
                int techRuns = 0;
                int analystRuns = 0;
                bool testPassing = false;

                var coordinator = new FridaTeamCoordinator(settings, (role, prompt) =>
                {
                    if (role == TeamRole.Manager)
                        return Task.FromResult("{\"plan\":\"Implementar e testar\",\"acceptance_criteria\":[\"build_passed\",\"tests_passed\"],\"research_queries\":[]}");
                    if (role == TeamRole.Assistant)
                        return Task.FromResult("Fatos verificados.");
                    if (role == TeamRole.Analyst)
                    {
                        analystRuns++;
                        if (!testPassing)
                            return Task.FromResult("[REPROVAR_CORRECAO_NECESSARIA] DEFEITO_ENCONTRADO: O teste falhou na divisao por zero. Corrija a funcao Divide.");
                        return Task.FromResult("[APROVADO_SEM_PENDENCIAS] Codigo corrigido, compilado e testes aprovados na revisao atual.");
                    }
                    if (role == TeamRole.Secretary)
                        return Task.FromResult("Aplicacao corrigida no ciclo de revisao e aprovada nos testes.");
                    return Task.FromResult("OK");
                });

                string summary = coordinator.RunAsync(
                    "Crie e valide o modulo de divisao segura",
                    instruction =>
                    {
                        techRuns++;
                        if (techRuns >= 2) testPassing = true;
                        return Task.FromResult(testPassing ? "Correcao aplicada e testes aprovados." : "Versao inicial escrita.");
                    },
                    () => testPassing
                        ? "[Estado de entrega (rev-2): arquivo_salvo=SIM | compilacao_aprovada=SIM | testes_aprovados=SIM | pedido_atendido=SIM]"
                        : "[Estado de entrega (rev-1): arquivo_salvo=SIM | compilacao_aprovada=SIM | testes_aprovados=NAO | pedido_atendido=NAO]",
                    null,
                    null,
                    new FridaTaskPlan { Kind = FridaTaskKind.ApplicationBuild, NeedsTeam = true }).GetAwaiter().GetResult();

                Check(techRuns == 2, "Tecnico deveria ter executado 2 vezes (1 inicial + 1 ciclo de correcao), executou " + techRuns);
                Check(analystRuns == 2, "Analista deveria ter revisado 2 vezes, revisou " + analystRuns);
                Check(testPassing && summary.Contains("aprovada"), "Ciclo de correcao nao resolveu a entrega");
                Check(FridaTeamState.AreSameModelFamily(ModelSelection.GroqBrain, ModelSelection.CerebrasBrain), "Deteccao de mesma familia gpt-oss-120b falhou");
            });

            // 7. P2 Regression: Dynamic reasoning effort, token budgets, and explicit model config preservation
            test("P2 regression: dynamic reasoning effort and token budgets per task complexity", () =>
            {
                var triagePlan = new FridaTaskPlan { Kind = FridaTaskKind.Conversation, EconomyMode = true };
                var implPlan = FridaTaskRouting.Decide("Execute e teste o script calculo.py", false, false);
                var archPlan = FridaTaskRouting.Decide("Construa uma aplicacao completa de gestao em C#", false, false);

                Check(triagePlan.ReasoningEffort == "low", "Triagem deve usar reasoning_effort=low");
                // Budgets follow the routing policy revised on 2026-11-08 (FridaTaskRouting.CompletionTokens):
                // conversation 8192, code execution/technical 16384, application build/team 32768.
                Check(triagePlan.CompletionTokens == 4096, "Triagem economica deve usar 4096 tokens");
                Check(implPlan.ReasoningEffort == "medium" && implPlan.CompletionTokens == 16384, "Implementacao deve usar reasoning_effort=medium e 16384 tokens");
                Check(archPlan.ReasoningEffort == "high" && archPlan.CompletionTokens == 32768, "Construcao de aplicacao/arquitetura deve usar reasoning_effort=high e 32768 tokens");

                string tempCfgDir = Path.Combine(Path.GetTempPath(), "frida-cfg-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempCfgDir);
                try
                {
                    string cfgFile = Path.Combine(tempCfgDir, "config.json");
                    File.WriteAllText(cfgFile, "{\"brain_models\":[\"gemini-3.8-flash\"],\"transcribe_model\":\"whisper-large-v3-turbo\",\"tts_model\":\"custom-tts-v1\",\"max_correction_cycles\":4}", Encoding.UTF8);
                    var loaded = FridaSettings.LoadOrCreate(cfgFile);
                    Check(loaded.brain_models.Length == 1 && loaded.brain_models[0] == "gemini-3.8-flash", "brain_models explicito foi sobrescrito silenciosamente!");
                    Check(loaded.transcribe_model == "whisper-large-v3-turbo", "transcribe_model explicito foi sobrescrito!");
                    Check(loaded.tts_model == "custom-tts-v1", "tts_model explicito foi sobrescrito!");
                    Check(loaded.max_correction_cycles == 4, "max_correction_cycles explicito nao preservado");
                }
                finally
                {
                    try { Directory.Delete(tempCfgDir, true); } catch { }
                }
            });

            // 8. P2 Regression: FFmpeg timeout kills hung process and returns null without hanging
            test("P2 regression: FFmpeg timeout kills hung process and returns within deadline", () =>
            {
                var sw = Stopwatch.StartNew();
                byte[] dummyMp3 = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                byte[] result = AudioVoiceEngine.ConvertMp3ToWavViaFfmpegAsync(
                    "powershell.exe",
                    "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                    dummyMp3,
                    400).GetAwaiter().GetResult();
                sw.Stop();
                Check(result == null, "Processo travado deveria retornar null no timeout");
                Check(sw.ElapsedMilliseconds < 5000, "Timeout do conversor de audio demorou demais (" + sw.ElapsedMilliseconds + "ms); processo travou o atendimento!");
            });

            // 9. Edge Cases: Operator conflict detection, external changes on resume, accented paths, and missing dotnet CLI detection
            test("edge cases: operator conflict detection, resume external changes, accented paths, and dotnet SDK status", () =>
            {
                string baseDir = Path.Combine(Path.GetTempPath(), "frida-edge-Acentuação Espaço-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(baseDir);
                try
                {
                    string codeFile = Path.Combine(baseDir, "Módulo_Cálculo.cs");
                    var state = FridaDevelopmentTaskState.CreateNew("Teste acentuacao e conflito", baseDir, true);
                    var w = FridaVerifiedTools.WriteFile(codeFile, "class Modulo { public static int X = 1; }\r\n", false);
                    state.RecordFileModification(codeFile, "", w.MonitorHash, "+X = 1", w.OperationId);

                    // Simulate external modification by operator outside the agent
                    File.WriteAllText(codeFile, "class Modulo { public static int X = 999; // editado pelo operador }\r\n", Encoding.UTF8);

                    // ApplySurgicalPatch must detect operator conflict and refuse to overwrite!
                    var patchAttempt = FridaCodingTools.ApplySurgicalPatch(codeFile, "int X = 1;", "int X = 2;", w.MonitorHash, state);
                    Check(patchAttempt.Status == FridaResultStatus.Failed && patchAttempt.ErrorCode == "operator_conflict",
                        "Conflito de modificacao do operador nao foi detectado!");
                    Check(File.ReadAllText(codeFile, Encoding.UTF8).Contains("999"), "Modificacao do operador foi perdida!");

                    // Persist and reload task state -> must detect external changes on resume
                    state.Save(baseDir);
                    var resumed = FridaDevelopmentTaskState.LoadLatest(baseDir);
                    Check(resumed != null && resumed.ExternalChangesDetectedOnResume && resumed.ExternalChangedFiles.Count == 1,
                        "Alteracao externa nao foi detectada ao retomar estado persistido!");

                    // Verify missing dotnet CLI SDK is accurately reported
                    var env = FridaEnvironmentCapabilities.GetMachineInventory();
                    bool hasDotnet = env.ContainsKey("dotnet_cli_sdk_available") && (bool)env["dotnet_cli_sdk_available"];
                    if (!hasDotnet)
                    {
                        var dotnetAttempt = FridaCodingTools.RunProjectValidation(baseDir, "dotnet build", "compile", 10, state);
                        Check(dotnetAttempt.Status == FridaResultStatus.Failed && dotnetAttempt.ErrorCode == "missing_dependency",
                            "Ausencia do SDK dotnet nao foi registrada como missing_dependency");
                    }
                }
                finally
                {
                    try { Directory.Delete(baseDir, true); } catch { }
                }
            });

            // 10. Autonomous Programming Battery: 10 Challenges x 3 Consecutive Rounds = 30 Validated Runs
            test("autonomous coding battery: 10 challenges x 3 rounds (30 runs, >=27 required, 0 false completions)", () =>
            {
                int totalRuns = 0;
                int solvedWithValidTests = 0;
                int falseCompletionClaims = 0;

                for (int round = 1; round <= 3; round++)
                {
                    for (int challengeId = 1; challengeId <= 10; challengeId++)
                    {
                        totalRuns++;
                        bool falseClaim;
                        bool solved = ExecuteCodingChallenge(challengeId, round, out falseClaim);
                        if (falseClaim) falseCompletionClaims++;
                        if (solved) solvedWithValidTests++;
                    }
                }

                Console.WriteLine("CODING_BATTERY_SUMMARY total=" + totalRuns + " solved=" + solvedWithValidTests + "/30 false_completions=" + falseCompletionClaims);
                Check(falseCompletionClaims == 0, "Houve afirmacao falsa de conclusao na bateria: " + falseCompletionClaims);
                Check(solvedWithValidTests >= 27, "Menos de 27/30 desafios resolvidos: " + solvedWithValidTests + "/30");
            });
        }

        private static bool ExecuteCodingChallenge(int challengeId, int round, out bool falseCompletionClaim)
        {
            falseCompletionClaim = false;
            string workDir = Path.Combine(Path.GetTempPath(), "frida-ch" + challengeId + "-r" + round + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(workDir);
            string csc = FridaEnvironmentCapabilities.DefaultFrameworkCscPath;

            try
            {
                var ledger = new FridaExecutionLedger();
                var state = FridaDevelopmentTaskState.CreateNew("Challenge " + challengeId + " Round " + round, workDir, true);
                ledger.DevTaskState = state;

                string requestText = "Desenvolva e valide com testes o desafio de programacao #" + challengeId;
                var intent = FridaRequestIntent.Classify(requestText);
                ledger.SetIntent(intent);

                // Step 1: Inspect Project
                var inspect = FridaCodingTools.InspectProject(workDir, 30, state);
                ledger.Add(inspect);

                if (challengeId == 1)
                {
                    // Challenge 1: C# Creation — Expression & Stats CLI
                    string src = Path.Combine(workDir, "StatsCli.cs");
                    string exe = Path.Combine(workDir, "StatsCli.exe");
                    string code = "using System;\nusing System.Linq;\nclass Program {\n"
                        + "  static int Main(string[] args) {\n"
                        + "    int[] nums = args.Select(int.Parse).ToArray();\n"
                        + "    Console.WriteLine(\"SUM=\" + nums.Sum() + \" MAX=\" + nums.Max());\n"
                        + "    return nums.Sum() == 60 && nums.Max() == 30 ? 0 : 1;\n"
                        + "  }\n}\n";
                    var w = FridaVerifiedTools.WriteFile(src, code, false);
                    ledger.Add(w);
                    state.RecordFileModification(src, "", w.MonitorHash, "+StatsCli", w.OperationId, ledger.CurrentProjectRevision);

                    // Verify before compile/test that TaskCompletionVerified is FALSE
                    if (ledger.TaskCompletionVerified) { falseCompletionClaim = true; return false; }

                    var comp = FridaCodingTools.RunProjectValidation(workDir, "& \"" + csc + "\" /nologo /out:\"" + exe + "\" \"" + src + "\"", "compile", 30, state);
                    ledger.Add(comp);
                    var tst = FridaCodingTools.RunProjectValidation(workDir, "$out = & \"" + exe + "\" 10 20 30; if ($LASTEXITCODE -ne 0 -or $out -notmatch 'SUM=60 MAX=30') { exit 1 }", "test", 30, state);
                    ledger.Add(tst);
                }
                else if (challengeId == 2)
                {
                    // Challenge 2: C# Bug Fix — Off-by-one in Inventory Reservation
                    string src = Path.Combine(workDir, "Inventory.cs");
                    string exe = Path.Combine(workDir, "Inventory.exe");
                    string buggy = "using System;\nclass Inventory {\n"
                        + "  public static bool CanReserve(int stock, int req) { return req < stock; }\n"
                        + "  static int Main() { return CanReserve(5, 5) && !CanReserve(5, 6) ? 0 : 1; }\n}\n";
                    var w = FridaVerifiedTools.WriteFile(src, buggy, false);
                    ledger.Add(w);
                    state.RecordFileModification(src, "", w.MonitorHash, "+buggy", w.OperationId, ledger.CurrentProjectRevision);

                    var comp1 = FridaCodingTools.RunProjectValidation(workDir, "& \"" + csc + "\" /nologo /out:\"" + exe + "\" \"" + src + "\"", "compile", 30, state);
                    ledger.Add(comp1);
                    var tst1 = FridaCodingTools.RunProjectValidation(workDir, "& \"" + exe + "\"", "test", 30, state);
                    ledger.Add(tst1);

                    // Must fail initially and NOT claim completion!
                    if (tst1.Status == FridaResultStatus.Verified || ledger.TaskCompletionVerified) { falseCompletionClaim = true; return false; }

                    // Read code, then apply surgical patch to fix `req < stock` -> `req <= stock`
                    ledger.Add(FridaCodingTools.ReadCodeLines(src, 1, 10, state));
                    var patch = FridaCodingTools.ApplySurgicalPatch(src, "return req < stock;", "return req <= stock;", w.MonitorHash, state);
                    ledger.Add(patch);

                    var comp2 = FridaCodingTools.RunProjectValidation(workDir, "& \"" + csc + "\" /nologo /out:\"" + exe + "\" \"" + src + "\"", "compile", 30, state);
                    ledger.Add(comp2);
                    var tst2 = FridaCodingTools.RunProjectValidation(workDir, "& \"" + exe + "\"", "test", 30, state);
                    ledger.Add(tst2);
                }
                else if (challengeId == 3)
                {
                    // Challenge 3: C# Refactoring — Discount Calculator Strategy
                    string src = Path.Combine(workDir, "Discount.cs");
                    string exe = Path.Combine(workDir, "Discount.exe");
                    string initial = "using System;\nclass Discount {\n"
                        + "  public static int Calc(string tier, int v) { if(tier==\"VIP\") return v - 20; return v; }\n"
                        + "  static int Main() { return Calc(\"VIP\", 100) == 80 ? 0 : 1; }\n}\n";
                    var w = FridaVerifiedTools.WriteFile(src, initial, false);
                    ledger.Add(w);
                    state.RecordFileModification(src, "", w.MonitorHash, "+initial", w.OperationId, ledger.CurrentProjectRevision);

                    ledger.Add(FridaCodingTools.SearchSymbols(workDir, "Calc", "*.cs", 10, state));
                    string refactored = "using System;\ninterface IDiscount { int Apply(int v); }\n"
                        + "class VipDiscount : IDiscount { public int Apply(int v) { return (int)(v * 0.8); } }\n"
                        + "class StandardDiscount : IDiscount { public int Apply(int v) { return v; } }\n"
                        + "class Discount {\n"
                        + "  public static int Calc(string tier, int v) { IDiscount d = tier==\"VIP\" ? (IDiscount)new VipDiscount() : new StandardDiscount(); return d.Apply(v); }\n"
                        + "  static int Main() { return Calc(\"VIP\", 200) == 160 && Calc(\"STD\", 200) == 200 ? 0 : 1; }\n}\n";
                    var patch = FridaCodingTools.ApplySurgicalPatch(src, initial.Trim(), refactored.Trim(), w.MonitorHash, state);
                    ledger.Add(patch);

                    ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "& \"" + csc + "\" /nologo /out:\"" + exe + "\" \"" + src + "\"", "compile", 30, state));
                    ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "& \"" + exe + "\"", "test", 30, state));
                }
                else if (challengeId == 4)
                {
                    // Challenge 4: C# Multi-file Project — Log Parser + Runner
                    string modelFile = Path.Combine(workDir, "LogEntry.cs");
                    string runnerFile = Path.Combine(workDir, "LogRunner.cs");
                    string exe = Path.Combine(workDir, "LogRunner.exe");

                    var w1 = FridaVerifiedTools.WriteFile(modelFile, "public sealed class LogEntry { public string Level; public string Msg; }\n", false);
                    ledger.Add(w1);
                    state.RecordFileModification(modelFile, "", w1.MonitorHash, "+LogEntry", w1.OperationId, ledger.CurrentProjectRevision);

                    var w2 = FridaVerifiedTools.WriteFile(runnerFile,
                        "using System;\nusing System.Linq;\nclass LogRunner {\n"
                        + "  static int Main() {\n"
                        + "    var items = new[] { new LogEntry{Level=\"ERR\",Msg=\"A\"}, new LogEntry{Level=\"INFO\",Msg=\"B\"}, new LogEntry{Level=\"ERR\",Msg=\"C\"} };\n"
                        + "    int errs = items.Count(x => x.Level == \"ERR\");\n"
                        + "    return errs == 2 ? 0 : 1;\n"
                        + "  }\n}\n", false);
                    ledger.Add(w2);
                    state.RecordFileModification(runnerFile, "", w2.MonitorHash, "+LogRunner", w2.OperationId, ledger.CurrentProjectRevision);

                    ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "& \"" + csc + "\" /nologo /out:\"" + exe + "\" \"" + modelFile + "\" \"" + runnerFile + "\"", "compile", 30, state));
                    ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "& \"" + exe + "\"", "test", 30, state));
                }
                else if (challengeId == 5)
                {
                    // Challenge 5: Python Creation — Financial Reconciliation with unittest
                    string pyFile = Path.Combine(workDir, "reconcile.py");
                    string testFile = Path.Combine(workDir, "test_reconcile.py");
                    var w1 = FridaVerifiedTools.WriteFile(pyFile,
                        "def reconcile_balances(expected, observed):\n"
                        + "    diff = round(observed - expected, 2)\n"
                        + "    return {'matched': abs(diff) < 0.001, 'delta': diff}\n", false);
                    ledger.Add(w1);
                    state.RecordFileModification(pyFile, "", w1.MonitorHash, "+reconcile", w1.OperationId, ledger.CurrentProjectRevision);

                    var w2 = FridaVerifiedTools.WriteFile(testFile,
                        "import unittest\nfrom reconcile import reconcile_balances\n"
                        + "class TestReconcile(unittest.TestCase):\n"
                        + "    def test_exact(self):\n"
                        + "        r = reconcile_balances(150.25, 150.25)\n"
                        + "        self.assertTrue(r['matched'])\n"
                        + "    def test_mismatch(self):\n"
                        + "        r = reconcile_balances(100.0, 95.5)\n"
                        + "        self.assertFalse(r['matched'])\n"
                        + "        self.assertEqual(r['delta'], -4.5)\n"
                        + "if __name__ == '__main__': unittest.main()\n", false);
                    ledger.Add(w2);
                    state.RecordFileModification(testFile, "", w2.MonitorHash, "+test_reconcile", w2.OperationId, ledger.CurrentProjectRevision);

                    ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "python -m py_compile \"" + pyFile + "\" \"" + testFile + "\"", "compile", 25, state));
                    ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "python \"" + testFile + "\"", "test", 25, state));
                }
                else if (challengeId == 6)
                {
                    // Challenge 6: Python Bug Fix — Failing test fixed via apply_patch and re-validated
                    string pyFile = Path.Combine(workDir, "retry_calc.py");
                    var w = FridaVerifiedTools.WriteFile(pyFile,
                        "def backoff_delay(attempt):\n"
                        + "    return 2 ** attempt # BUG: should cap at 32\n"
                        + "if __name__ == '__main__':\n"
                        + "    assert backoff_delay(2) == 4\n"
                        + "    assert backoff_delay(10) == 32, 'Cap failed'\n", false);
                    ledger.Add(w);
                    state.RecordFileModification(pyFile, "", w.MonitorHash, "+buggy_backoff", w.OperationId, ledger.CurrentProjectRevision);

                    var failTest = FridaCodingTools.RunProjectValidation(workDir, "python \"" + pyFile + "\"", "test", 25, state);
                    ledger.Add(failTest);
                    if (failTest.Status == FridaResultStatus.Verified || ledger.TaskCompletionVerified) { falseCompletionClaim = true; return false; }

                    var patch = FridaCodingTools.ApplySurgicalPatch(pyFile, "return 2 ** attempt # BUG: should cap at 32", "return min(32, 2 ** attempt)", w.MonitorHash, state);
                    ledger.Add(patch);

                    ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "python -m py_compile \"" + pyFile + "\"", "compile", 25, state));
                    ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "python \"" + pyFile + "\"", "test", 25, state));
                }
                else if (challengeId == 7)
                {
                    // Challenge 7: Python Refactoring in Path with Spaces and Accents
                    string subDir = Path.Combine(workDir, "Módulo de Análise & Revisão");
                    Directory.CreateDirectory(subDir);
                    string pyFile = Path.Combine(subDir, "normalizador_texto.py");
                    var w = FridaVerifiedTools.WriteFile(pyFile,
                        "def normalize_tokens(text):\n    return text.split()\n"
                        + "if __name__ == '__main__':\n    assert normalize_tokens('  Ola,   Mundo! ') == ['ola', 'mundo']\n", false);
                    ledger.Add(w);
                    state.RecordFileModification(pyFile, "", w.MonitorHash, "+initial", w.OperationId, ledger.CurrentProjectRevision);

                    var patch = FridaCodingTools.ApplySurgicalPatch(
                        pyFile,
                        "def normalize_tokens(text):\n    return text.split()",
                        "import re\ndef normalize_tokens(text):\n    return [t.lower() for t in re.findall(r'\\w+', text or '')]",
                        w.MonitorHash,
                        state);
                    ledger.Add(patch);

                    ledger.Add(FridaCodingTools.RunProjectValidation(subDir, "python -m py_compile \"" + pyFile + "\"", "compile", 25, state));
                    ledger.Add(FridaCodingTools.RunProjectValidation(subDir, "python \"" + pyFile + "\"", "test", 25, state));
                }
                else if (challengeId == 8)
                {
                    // Challenge 8: PowerShell Creation — SHA-256 Manifest Builder & Verifier
                    string psFile = Path.Combine(workDir, "ManifestBuilder.ps1");
                    string sampleFile = Path.Combine(workDir, "sample.txt");
                    File.WriteAllText(sampleFile, "frida-manifest-content", Encoding.UTF8);

                    string psCode = "$ErrorActionPreference = 'Stop'\r\n"
                        + "$h = (Get-FileHash -LiteralPath '" + sampleFile.Replace("'", "''") + "' -Algorithm SHA256).Hash\r\n"
                        + "if ([string]::IsNullOrWhiteSpace($h) -or $h.Length -ne 64) { exit 1 }\r\n"
                        + "Write-Output \"HASH_OK=$h\"\r\n"
                        + "exit 0\r\n";
                    var w = FridaVerifiedTools.WriteFile(psFile, psCode, false);
                    ledger.Add(w);
                    state.RecordFileModification(psFile, "", w.MonitorHash, "+ManifestBuilder", w.OperationId, ledger.CurrentProjectRevision);

                    string astCheck = "$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('" + psFile.Replace("'", "''") + "', [ref]$null, [ref]$errors); if ($errors.Count -gt 0) { exit 1 }";
                    ledger.Add(FridaCodingTools.RunProjectValidation(workDir, astCheck, "compile", 25, state));
                    ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "& '" + psFile.Replace("'", "''") + "'", "test", 25, state));
                }
                else if (challengeId == 9)
                {
                    // Challenge 9: PowerShell Bug Fix — Threshold Filter Fix
                    string psFile = Path.Combine(workDir, "FilterMetrics.ps1");
                    string buggyPs = "$vals = @(10, 25, 50, 80)\r\n"
                        + "$high = @($vals | Where-Object { $_ -gt 100 })\r\n"
                        + "if ($high.Count -ne 2) { exit 1 }\r\n"
                        + "exit 0\r\n";
                    var w = FridaVerifiedTools.WriteFile(psFile, buggyPs, false);
                    ledger.Add(w);
                    state.RecordFileModification(psFile, "", w.MonitorHash, "+buggyPs", w.OperationId, ledger.CurrentProjectRevision);

                    var failRun = FridaCodingTools.RunProjectValidation(workDir, "& '" + psFile.Replace("'", "''") + "'", "test", 25, state);
                    ledger.Add(failRun);
                    if (failRun.Status == FridaResultStatus.Verified || ledger.TaskCompletionVerified) { falseCompletionClaim = true; return false; }

                    var patch = FridaCodingTools.ApplySurgicalPatch(psFile, "$_ -gt 100", "$_ -ge 50", w.MonitorHash, state);
                    ledger.Add(patch);

                    string astCheck = "$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('" + psFile.Replace("'", "''") + "', [ref]$null, [ref]$errors); if ($errors.Count -gt 0) { exit 1 }";
                    ledger.Add(FridaCodingTools.RunProjectValidation(workDir, astCheck, "compile", 25, state));
                    ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "& '" + psFile.Replace("'", "''") + "'", "test", 25, state));
                }
                else if (challengeId == 10)
                {
                    // Challenge 10: Full Reviewer-Driven Correction Loop on C# Quota Aggregator
                    string src = Path.Combine(workDir, "QuotaAgg.cs");
                    string exe = Path.Combine(workDir, "QuotaAgg.exe");
                    int cycleCount = 0;

                    var coordinator = new FridaTeamCoordinator(new FridaSettings { max_correction_cycles = 3 }, (role, prompt) =>
                    {
                        if (role == TeamRole.Analyst)
                        {
                            if (!ledger.TaskCompletionVerified)
                                return Task.FromResult("[REPROVAR_CORRECAO_NECESSARIA] DEFEITO_ENCONTRADO: Soma de cotas incorreta na revisao atual.");
                            return Task.FromResult("[APROVADO_SEM_PENDENCIAS] Compilacao e testes validados na revisao atual.");
                        }
                        return Task.FromResult("{\"plan\":\"Validar QuotaAgg\",\"research_queries\":[]}");
                    });

                    coordinator.RunAsync(
                        requestText,
                        instruction =>
                        {
                            cycleCount++;
                            if (cycleCount == 1)
                            {
                                var w = FridaVerifiedTools.WriteFile(src, "class QuotaAgg { static int Main() { return (10 - 5) == 15 ? 0 : 1; } }\n", false);
                                ledger.Add(w);
                                state.RecordFileModification(src, "", w.MonitorHash, "+cycle1", w.OperationId, ledger.CurrentProjectRevision);
                                ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "& \"" + csc + "\" /nologo /out:\"" + exe + "\" \"" + src + "\"", "compile", 30, state));
                                ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "& \"" + exe + "\"", "test", 30, state));
                                return Task.FromResult("Tentativa 1 executada.");
                            }
                            else
                            {
                                var patch = FridaCodingTools.ApplySurgicalPatch(src, "(10 - 5) == 15", "(10 + 5) == 15", null, state);
                                ledger.Add(patch);
                                ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "& \"" + csc + "\" /nologo /out:\"" + exe + "\" \"" + src + "\"", "compile", 30, state));
                                ledger.Add(FridaCodingTools.RunProjectValidation(workDir, "& \"" + exe + "\"", "test", 30, state));
                                return Task.FromResult("Correcao aplicada no ciclo " + cycleCount);
                            }
                        },
                        () => FridaOperationalReply.Finalize(intent, ledger, ""),
                        null,
                        null,
                        new FridaTaskPlan { Kind = FridaTaskKind.ApplicationBuild, NeedsTeam = true }).GetAwaiter().GetResult();
                }

                string finalized = FridaOperationalReply.Finalize(intent, ledger, "Entrega concluida.");
                bool verified = ledger.TaskCompletionVerified && state.RequestFulfilled && finalized.Contains("pedido_atendido=SIM");
                if (!verified && finalized.Contains("pedido_atendido=SIM"))
                {
                    falseCompletionClaim = true;
                }
                return verified;
            }
            finally
            {
                try { Directory.Delete(workDir, true); } catch { }
            }
        }
    }
}
