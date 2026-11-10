using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public sealed class FridaTrackedFileEntry
    {
        public string FilePath { get; set; }
        public string InitialSha256 { get; set; }
        public string LastAgentSha256 { get; set; }
        public string LastDiff { get; set; }
        public string LastOperationId { get; set; }
        public string LastModifiedUtc { get; set; }

        public FridaTrackedFileEntry()
        {
            FilePath = "";
            InitialSha256 = "";
            LastAgentSha256 = "";
            LastDiff = "";
            LastOperationId = "";
            LastModifiedUtc = "";
        }
    }

    public sealed class FridaValidationAttemptRecord
    {
        public int AttemptNumber { get; set; }
        public string StepId { get; set; }
        public string OperationId { get; set; }
        public string Kind { get; set; }
        public string ProjectRevision { get; set; }
        public bool Passed { get; set; }
        public string Summary { get; set; }
        public string SupersededByOperationId { get; set; }
        public string TimestampUtc { get; set; }

        public FridaValidationAttemptRecord()
        {
            StepId = "";
            OperationId = "";
            Kind = "";
            ProjectRevision = "";
            Summary = "";
            SupersededByOperationId = "";
            TimestampUtc = "";
        }
    }

    public sealed class FridaReviewFinding
    {
        public string FindingId { get; set; }
        public int Cycle { get; set; }
        public string Severity { get; set; }
        public string Description { get; set; }
        public bool Resolved { get; set; }
        public string ResolvedByRevision { get; set; }

        public FridaReviewFinding()
        {
            FindingId = "";
            Severity = "defect";
            Description = "";
            ResolvedByRevision = "";
        }
    }

    public sealed class FridaDevelopmentTaskState
    {
        public string SchemaVersion { get; set; }
        public string TaskId { get; set; }
        public string Objective { get; set; }
        public string ProjectRoot { get; set; }
        public string SshAlias { get; set; }
        public bool ResumeVerificationPending { get; set; }
        public string DetectedStack { get; set; }
        public int RevisionNumber { get; set; }
        public string ProjectRevision { get; set; }
        public bool IsSoftwareDevelopment { get; set; }
        public bool RequireCompilation { get; set; }
        public bool RequireTests { get; set; }
        public List<string> AcceptanceCriteria { get; set; }

        public bool FileSaved { get; set; }
        public bool ProcessCompleted { get; set; }
        public bool CompilationPassed { get; set; }
        public string CompiledRevision { get; set; }
        public bool TestsPassed { get; set; }
        public string TestedRevision { get; set; }
        public bool ReviewerApproved { get; set; }
        public string ReviewedRevision { get; set; }

        public int CorrectionCyclesUsed { get; set; }
        public int MaxCorrectionCycles { get; set; }
        public int ToolCallsUsed { get; set; }
        public int MaxToolCalls { get; set; }
        public int MaxTaskMinutes { get; set; }
        public string StartedAtUtc { get; set; }
        public string UpdatedAtUtc { get; set; }
        public bool BudgetExhausted { get; set; }
        public string PendingWorkSummary { get; set; }
        public bool ExternalChangesDetectedOnResume { get; set; }
        public List<string> ExternalChangedFiles { get; set; }
        public List<string> UncertainOperationsPendingReconciliation { get; set; }

        public List<FridaTrackedFileEntry> TrackedFiles { get; set; }
        public List<FridaValidationAttemptRecord> ValidationHistory { get; set; }
        public List<FridaReviewFinding> ReviewFindings { get; set; }

        public FridaDevelopmentTaskState()
        {
            SchemaVersion = "1.0";
            TaskId = "";
            Objective = "";
            ProjectRoot = "";
            DetectedStack = "";
            ProjectRevision = "rev-0";
            AcceptanceCriteria = new List<string>();
            CompiledRevision = "";
            TestedRevision = "";
            ReviewedRevision = "";
            MaxCorrectionCycles = 3;
            MaxToolCalls = 40;
            MaxTaskMinutes = 30;
            StartedAtUtc = DateTime.UtcNow.ToString("o");
            UpdatedAtUtc = DateTime.UtcNow.ToString("o");
            PendingWorkSummary = "";
            ExternalChangedFiles = new List<string>();
            UncertainOperationsPendingReconciliation = new List<string>();
            TrackedFiles = new List<FridaTrackedFileEntry>();
            ValidationHistory = new List<FridaValidationAttemptRecord>();
            ReviewFindings = new List<FridaReviewFinding>();
        }

        public static FridaDevelopmentTaskState CreateNew(
            string objective,
            string projectRoot,
            bool isSoftwareDev,
            int maxCorrectionCycles = 3,
            int maxToolCalls = 40,
            int maxTaskMinutes = 30)
        {
            var now = DateTime.UtcNow.ToString("o");
            var state = new FridaDevelopmentTaskState
            {
                TaskId = "dev-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6),
                Objective = objective ?? "",
                ProjectRoot = string.IsNullOrWhiteSpace(projectRoot) ? Environment.CurrentDirectory : projectRoot,
                SshAlias = FridaSshWorkspace.IsRemote ? FridaIdeRuntime.Context.Project.SshAlias : null,
                IsSoftwareDevelopment = isSoftwareDev,
                RequireCompilation = isSoftwareDev,
                RequireTests = isSoftwareDev,
                RevisionNumber = 0,
                ProjectRevision = "rev-0",
                MaxCorrectionCycles = Math.Max(1, maxCorrectionCycles),
                MaxToolCalls = Math.Max(5, maxToolCalls),
                MaxTaskMinutes = Math.Max(1, maxTaskMinutes),
                StartedAtUtc = now,
                UpdatedAtUtc = now
            };
            if (isSoftwareDev)
            {
                state.AcceptanceCriteria.Add("Arquivos solicitados salvos com hash SHA-256 verificado (file_saved)");
                state.AcceptanceCriteria.Add("Compilacao ou verificacao sintatica aprovada na revisao atual (build_passed)");
                state.AcceptanceCriteria.Add("Testes ou execucao funcional validada na revisao atual (tests_passed)");
                state.AcceptanceCriteria.Add("Sem defeitos abertos apontados na revisao final (request_fulfilled)");
            }
            state.UpdatePendingSummary();
            return state;
        }

        public bool IsCompilationValidForCurrentRevision
        {
            get { return !RequireCompilation || (CompilationPassed && string.Equals(CompiledRevision, ProjectRevision, StringComparison.OrdinalIgnoreCase)); }
        }

        public bool IsTestsValidForCurrentRevision
        {
            get { return !RequireTests || (TestsPassed && string.Equals(TestedRevision, ProjectRevision, StringComparison.OrdinalIgnoreCase)); }
        }

        public bool HasUnresolvedReviewFindings
        {
            get { return ReviewFindings != null && ReviewFindings.Any(f => f != null && !f.Resolved); }
        }

        public bool RequestFulfilled
        {
            get
            {
                if (UncertainOperationsPendingReconciliation != null && UncertainOperationsPendingReconciliation.Count > 0)
                    return false;
                if (!IsSoftwareDevelopment)
                    return FileSaved || ProcessCompleted;
                return FileSaved
                    && IsCompilationValidForCurrentRevision
                    && IsTestsValidForCurrentRevision
                    && !HasUnresolvedReviewFindings;
            }
        }

        public bool RegisterToolCallAndCheckBudget(out string limitReason)
        {
            limitReason = "";
            ToolCallsUsed++;
            UpdatedAtUtc = DateTime.UtcNow.ToString("o");

            if (ToolCallsUsed > MaxToolCalls)
            {
                BudgetExhausted = true;
                limitReason = "Limite de chamadas de ferramentas atingido (" + MaxToolCalls + ").";
                UpdatePendingSummary(limitReason);
                return false;
            }

            DateTime started;
            if (DateTime.TryParse(StartedAtUtc, out started))
            {
                var elapsed = DateTime.UtcNow - started.ToUniversalTime();
                if (elapsed.TotalMinutes > MaxTaskMinutes)
                {
                    BudgetExhausted = true;
                    limitReason = "Limite de tempo da tarefa atingido (" + MaxTaskMinutes + " min).";
                    UpdatePendingSummary(limitReason);
                    return false;
                }
            }
            return true;
        }

        public void RecordFileModification(string filePath, string beforeSha256, string afterSha256, string unifiedDiff, string operationId, string explicitRevision = null)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            string fullPath = FridaSshWorkspace.IsRemote ? FridaSshWorkspace.ResolvePath(filePath) : Path.GetFullPath(filePath);
            var existing = TrackedFiles.FirstOrDefault(f => string.Equals(f.FilePath, fullPath, FridaSshWorkspace.IsRemote ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new FridaTrackedFileEntry
                {
                    FilePath = fullPath,
                    InitialSha256 = beforeSha256 ?? ""
                };
                TrackedFiles.Add(existing);
            }

            existing.LastAgentSha256 = afterSha256 ?? "";
            existing.LastDiff = unifiedDiff ?? "";
            existing.LastOperationId = operationId ?? "";
            existing.LastModifiedUtc = DateTime.UtcNow.ToString("o");

            RevisionNumber++;
            if (!string.IsNullOrWhiteSpace(explicitRevision))
            {
                ProjectRevision = explicitRevision;
            }
            else
            {
                string shortHash = string.IsNullOrEmpty(afterSha256) ? "000000" : afterSha256.Substring(0, Math.Min(8, afterSha256.Length));
                ProjectRevision = "rev-" + RevisionNumber + "-" + shortHash;
            }
            FileSaved = true;

            // Modifying code invalidates previous build/test/review approvals
            CompilationPassed = false;
            TestsPassed = false;
            ReviewerApproved = false;
            UpdatedAtUtc = DateTime.UtcNow.ToString("o");
            UpdatePendingSummary();
        }

        public void RecordValidation(string stepId, string operationId, string kind, bool passed, string summary)
        {
            string normalizedKind = (kind ?? "process").Trim().ToLowerInvariant();
            ProcessCompleted = true;

            if (passed)
            {
                foreach (var prev in ValidationHistory)
                {
                    if (prev != null && !prev.Passed && string.Equals(prev.Kind, normalizedKind, StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrEmpty(prev.SupersededByOperationId))
                    {
                        prev.SupersededByOperationId = operationId ?? "latest";
                    }
                }
            }

            ValidationHistory.Add(new FridaValidationAttemptRecord
            {
                AttemptNumber = ValidationHistory.Count + 1,
                StepId = stepId ?? ("val-" + (ValidationHistory.Count + 1)),
                OperationId = operationId ?? "",
                Kind = normalizedKind,
                ProjectRevision = ProjectRevision,
                Passed = passed,
                Summary = summary ?? "",
                TimestampUtc = DateTime.UtcNow.ToString("o")
            });

            if (normalizedKind == "compile" || normalizedKind == "build" || normalizedKind == "build_passed")
            {
                CompilationPassed = passed;
                CompiledRevision = passed ? ProjectRevision : "";
            }
            else if (normalizedKind == "test" || normalizedKind == "tests" || normalizedKind == "tests_passed" || normalizedKind == "tests_passed_with_compile")
            {
                TestsPassed = passed;
                TestedRevision = passed ? ProjectRevision : "";
                if (passed && (normalizedKind == "tests_passed_with_compile" || DetectedStack == "python" || DetectedStack == "powershell") && !CompilationPassed)
                {
                    CompilationPassed = true;
                    CompiledRevision = ProjectRevision;
                }
            }
            UpdatedAtUtc = DateTime.UtcNow.ToString("o");
            UpdatePendingSummary();
        }

        public void RecordReviewOutcome(int cycle, bool approved, IEnumerable<string> findings)
        {
            CorrectionCyclesUsed = Math.Max(CorrectionCyclesUsed, cycle);
            if (approved)
            {
                ReviewerApproved = true;
                ReviewedRevision = ProjectRevision;
                foreach (var f in ReviewFindings)
                {
                    if (f != null && !f.Resolved)
                    {
                        f.Resolved = true;
                        f.ResolvedByRevision = ProjectRevision;
                    }
                }
            }
            else
            {
                ReviewerApproved = false;
                if (findings != null)
                {
                    int idx = ReviewFindings.Count + 1;
                    foreach (var item in findings)
                    {
                        if (string.IsNullOrWhiteSpace(item)) continue;
                        ReviewFindings.Add(new FridaReviewFinding
                        {
                            FindingId = "rev-finding-" + idx++,
                            Cycle = cycle,
                            Severity = "defect",
                            Description = item.Trim(),
                            Resolved = false
                        });
                    }
                }
            }
            UpdatedAtUtc = DateTime.UtcNow.ToString("o");
            UpdatePendingSummary();
        }

        public void UpdatePendingSummary(string extraReason = null)
        {
            var pending = new List<string>();
            if (!string.IsNullOrWhiteSpace(extraReason))
                pending.Add(extraReason.Trim());
            if (IsSoftwareDevelopment)
            {
                if (!FileSaved) pending.Add("Nenhum arquivo de entregavel salvo.");
                if (!IsCompilationValidForCurrentRevision) pending.Add("Compilacao pendente para a revisao " + ProjectRevision + ".");
                if (!IsTestsValidForCurrentRevision) pending.Add("Testes funcionais pendentes para a revisao " + ProjectRevision + ".");
                if (HasUnresolvedReviewFindings)
                {
                    var open = ReviewFindings.Where(f => f != null && !f.Resolved).Select(f => f.Description).ToArray();
                    pending.Add("Achados do revisor pendentes: " + string.Join("; ", open));
                }
            }
            if (UncertainOperationsPendingReconciliation != null && UncertainOperationsPendingReconciliation.Count > 0)
            {
                pending.Add("Operacoes incertas aguardando reconciliacao: " + string.Join(", ", UncertainOperationsPendingReconciliation));
            }
            PendingWorkSummary = pending.Count == 0 ? "Concluido e validado na revisao " + ProjectRevision + "." : string.Join(" | ", pending);
        }

        public bool CheckExternalChangesOnResume()
        {
            ExternalChangesDetectedOnResume = false;
            ResumeVerificationPending = false;
            ExternalChangedFiles.Clear();
            foreach (var tracked in TrackedFiles)
            {
                if (tracked == null || string.IsNullOrWhiteSpace(tracked.FilePath)) continue;
                string key = "resume:" + tracked.FilePath;
                string currentHash;
                try
                {
                    currentHash = !string.IsNullOrEmpty(SshAlias)
                        ? FridaSshWorkspace.Hash(new FridaIdeProject { Name = Objective, Root = ProjectRoot, SshAlias = SshAlias }, tracked.FilePath) ?? ""
                        : File.Exists(tracked.FilePath) ? FridaVerifiedTools.FileSha256(tracked.FilePath) : "";
                    UncertainOperationsPendingReconciliation.Remove(key);
                }
                catch (IOException)
                {
                    ResumeVerificationPending = true;
                    if (!UncertainOperationsPendingReconciliation.Contains(key)) UncertainOperationsPendingReconciliation.Add(key);
                    CompilationPassed = false; TestsPassed = false; ReviewerApproved = false;
                    continue;
                }
                if (!string.Equals(currentHash, tracked.LastAgentSha256 ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    ExternalChangesDetectedOnResume = true;
                    ExternalChangedFiles.Add(tracked.FilePath);
                    CompilationPassed = false;
                    TestsPassed = false;
                    ReviewerApproved = false;
                }
            }
            if (ExternalChangesDetectedOnResume)
            {
                RevisionNumber++;
                ProjectRevision = "rev-" + RevisionNumber + "-external";
                UpdatePendingSummary("Alteracoes externas detectadas antes da retomada.");
            }
            if (ResumeVerificationPending) UpdatePendingSummary("Aguardando reconexão para conferir os arquivos remotos.");
            return ExternalChangesDetectedOnResume;
        }

        public void Save(string storageDirectory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(storageDirectory)) return;
                Directory.CreateDirectory(storageDirectory);
                UpdatePendingSummary();
                var json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(this);
                string taskFile = Path.Combine(storageDirectory, (string.IsNullOrWhiteSpace(TaskId) ? "dev-task" : TaskId) + ".json");
                string latestFile = Path.Combine(storageDirectory, "active_dev_task.json");
                FridaRuntimeHealth.AtomicWrite(taskFile, json);
                FridaRuntimeHealth.AtomicWrite(latestFile, json);
            }
            catch { }
        }

        public static FridaDevelopmentTaskState LoadLatest(string storageDirectory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(storageDirectory)) return null;
                string latestFile = Path.Combine(storageDirectory, "active_dev_task.json");
                if (!File.Exists(latestFile)) return null;
                string json = File.ReadAllText(latestFile, Encoding.UTF8);
                var state = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<FridaDevelopmentTaskState>(json);
                if (state != null)
                {
                    state.CheckExternalChangesOnResume();
                }
                return state;
            }
            catch
            {
                return null;
            }
        }
    }

    public static class FridaEnvironmentCapabilities
    {
        private static readonly object Sync = new object();
        private static Dictionary<string, object> _cachedInventory;
        private static DateTime _lastCheckUtc = DateTime.MinValue;

        public const string DefaultFrameworkCscPath = @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe";

        public static Dictionary<string, object> GetMachineInventory(bool forceRefresh = false)
        {
            lock (Sync)
            {
                if (!forceRefresh && _cachedInventory != null && (DateTime.UtcNow - _lastCheckUtc).TotalMinutes < 10)
                    return _cachedInventory;

                bool hasCsc = File.Exists(DefaultFrameworkCscPath);
                string pythonCmd = ProbeExecutable("python", "--version");
                string powershellCmd = ProbeExecutable("powershell", "-NoProfile -Command \"$PSVersionTable.PSVersion.ToString()\"");
                string gitCmd = ProbeExecutable("git", "--version");
                string ffmpegCmd = ProbeExecutable("ffmpeg", "-version");
                string dotnetCmd = ProbeExecutable("dotnet", "--info");
                bool hasDotnetSdk = !string.IsNullOrWhiteSpace(dotnetCmd)
                    && dotnetCmd.IndexOf("not recognized", StringComparison.OrdinalIgnoreCase) < 0
                    && dotnetCmd.IndexOf("No .NET SDKs were found", StringComparison.OrdinalIgnoreCase) < 0
                    && dotnetCmd.IndexOf("No SDKs were found", StringComparison.OrdinalIgnoreCase) < 0
                    && dotnetCmd.IndexOf("probe_failed", StringComparison.OrdinalIgnoreCase) < 0;

                _cachedInventory = new Dictionary<string, object>
                {
                    { "os", Environment.OSVersion.ToString() },
                    { "is_64bit_os", Environment.Is64BitOperatingSystem },
                    { "csc_net48_available", hasCsc },
                    { "csc_net48_path", hasCsc ? DefaultFrameworkCscPath : "" },
                    { "dotnet_cli_sdk_available", hasDotnetSdk },
                    { "dotnet_status_note", hasDotnetSdk ? "SDK dotnet disponivel" : "AUSENTE: ha compilador C# legado (csc.exe .NET Framework 4.8) e Python disponiveis, mas nao ha SDK instalado pelo comando dotnet." },
                    { "python_available", !string.IsNullOrWhiteSpace(pythonCmd) && !pythonCmd.StartsWith("probe_failed") },
                    { "python_version", FirstLine(pythonCmd) },
                    { "powershell_available", !string.IsNullOrWhiteSpace(powershellCmd) && !powershellCmd.StartsWith("probe_failed") },
                    { "powershell_version", FirstLine(powershellCmd) },
                    { "git_available", !string.IsNullOrWhiteSpace(gitCmd) && !gitCmd.StartsWith("probe_failed") },
                    { "git_version", FirstLine(gitCmd) },
                    { "ffmpeg_available", !string.IsNullOrWhiteSpace(ffmpegCmd) && !ffmpegCmd.StartsWith("probe_failed") },
                    { "ffmpeg_version", FirstLine(ffmpegCmd) }
                };
                _lastCheckUtc = DateTime.UtcNow;
                return _cachedInventory;
            }
        }

        private static string ProbeExecutable(string fileName, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return "probe_failed:null_process";
                    var outTask = proc.StandardOutput.ReadToEndAsync();
                    var errTask = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(3500))
                    {
                        FridaVerifiedTools.KillOwnedProcessTree(proc);
                        return "probe_failed:timeout";
                    }
                    TaskWaitSafe(outTask, 1000);
                    TaskWaitSafe(errTask, 1000);
                    string stdout = outTask.Status == TaskStatus.RanToCompletion ? (outTask.Result ?? "") : "";
                    string stderr = errTask.Status == TaskStatus.RanToCompletion ? (errTask.Result ?? "") : "";
                    if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
                        return "probe_failed:exit_" + proc.ExitCode + ":" + stderr.Trim();
                    return string.IsNullOrWhiteSpace(stdout) ? stderr.Trim() : stdout.Trim();
                }
            }
            catch (Exception ex)
            {
                return "probe_failed:" + ex.Message;
            }
        }

        private static void TaskWaitSafe(Task task, int ms)
        {
            try { if (task != null) task.Wait(ms); } catch { }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[0].Trim() : text.Trim();
        }
    }

    public static class FridaContextPaginator
    {
        public static string PaginateToolMessageContent(string content, int maxChars = 4000, int requestedPage = 1)
        {
            if (string.IsNullOrEmpty(content)) return "";
            int safeMax = Math.Max(650, maxChars);
            if (content.Length <= safeMax) return content;

            var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            try
            {
                var root = serializer.DeserializeObject(content) as Dictionary<string, object>;
                if (root != null && (root.ContainsKey("operation_id") || root.ContainsKey("tool") || root.ContainsKey("status")))
                {
                    string rawUntrusted = GetString(root, "untrusted_output");
                    var dataDict = root.ContainsKey("data") ? root["data"] as Dictionary<string, object> : null;
                    if (string.IsNullOrEmpty(rawUntrusted) && dataDict != null)
                    {
                        rawUntrusted = GetString(dataDict, "stdout");
                        if (!string.IsNullOrEmpty(GetString(dataDict, "stderr")))
                            rawUntrusted = (rawUntrusted + "\n[STDERR]\n" + GetString(dataDict, "stderr")).Trim();
                    }

                    int totalChars = string.IsNullOrEmpty(rawUntrusted) ? content.Length : rawUntrusted.Length;
                    string[] lines = string.IsNullOrEmpty(rawUntrusted)
                        ? new string[0]
                        : rawUntrusted.Replace("\r\n", "\n").Split('\n');
                    int totalLines = lines.Length;

                    int pageCharsBudget = Math.Max(220, safeMax - 560);
                    int totalPages = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, totalChars) / pageCharsBudget));
                    int page = Math.Max(1, Math.Min(requestedPage, totalPages));

                    string pagedExcerpt = BuildStructuredExcerpt(rawUntrusted, lines, pageCharsBudget, page, totalPages);

                    var compactData = new Dictionary<string, object>();
                    if (dataDict != null)
                    {
                        foreach (var kv in dataDict)
                        {
                            if (string.Equals(kv.Key, "stdout", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(kv.Key, "stderr", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(kv.Key, "content", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(kv.Key, "files", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            string valStr = kv.Value == null ? "" : kv.Value.ToString();
                            if (valStr.Length > 200)
                                compactData[kv.Key] = valStr.Substring(0, 200) + "...";
                            else
                                compactData[kv.Key] = kv.Value;
                        }
                    }

                    var preserved = new Dictionary<string, object>
                    {
                        { "schema_version", root.ContainsKey("schema_version") ? root["schema_version"] : 1 },
                        { "request_id", GetString(root, "request_id", "") },
                        { "operation_id", GetString(root, "operation_id", "") },
                        { "step_id", GetString(root, "step_id", "") },
                        { "attempt", root.ContainsKey("attempt") ? root["attempt"] : 1 },
                        { "project_revision", GetString(root, "project_revision", "rev-0") },
                        { "tool", GetString(root, "tool", "") },
                        { "operation", GetString(root, "operation", "") },
                        { "target", GetString(root, "target", "") },
                        { "status", GetString(root, "status", "") },
                        { "verified_scope", GetString(root, "verified_scope", "") },
                        { "is_action", root.ContainsKey("is_action") ? root["is_action"] : false },
                        { "summary", TruncatePlain(GetString(root, "summary", ""), 260) },
                        { "error_code", GetString(root, "error_code", null) },
                        { "superseded_by", GetString(root, "superseded_by", null) },
                        { "checks", root.ContainsKey("checks") ? root["checks"] : new object[0] },
                        { "validation_evidence_refs", root.ContainsKey("validation_evidence_refs") ? root["validation_evidence_refs"] : new object[0] },
                        { "data", compactData },
                        { "pagination", new Dictionary<string, object>
                            {
                                { "paginated", true },
                                { "page", page },
                                { "total_pages", totalPages },
                                { "total_chars", totalChars },
                                { "total_lines", totalLines },
                                { "has_more", page < totalPages },
                                { "full_output_ref", "operation://" + GetString(root, "operation_id", "latest") }
                            }
                        },
                        { "untrusted_output", pagedExcerpt }
                    };

                    string serialized = serializer.Serialize(preserved);
                    if (serialized.Length > safeMax)
                    {
                        int overflow = serialized.Length - safeMax + 60;
                        int adjustedBudget = Math.Max(180, pageCharsBudget - overflow);
                        preserved["untrusted_output"] = BuildStructuredExcerpt(rawUntrusted, lines, adjustedBudget, page, totalPages);
                        serialized = serializer.Serialize(preserved);
                        if (serialized.Length > safeMax)
                        {
                            int secondOverflow = serialized.Length - safeMax + 40;
                            preserved["untrusted_output"] = BuildStructuredExcerpt(rawUntrusted, lines, Math.Max(120, adjustedBudget - secondOverflow), page, totalPages);
                            serialized = serializer.Serialize(preserved);
                        }
                    }
                    return serialized;
                }
            }
            catch
            {
                // Fall through to structured envelope for non-receipt content
            }

            int rawBudget = Math.Max(180, safeMax - 220);
            string[] rawLines = content.Replace("\r\n", "\n").Split('\n');
            string excerpt = BuildStructuredExcerpt(content, rawLines, rawBudget, 1, Math.Max(1, (int)Math.Ceiling((double)content.Length / rawBudget)));
            var wrapper = new Dictionary<string, object>
            {
                { "paginated_text", true },
                { "total_chars", content.Length },
                { "total_lines", rawLines.Length },
                { "excerpt", excerpt }
            };
            return serializer.Serialize(wrapper);
        }

        private static string BuildStructuredExcerpt(string raw, string[] lines, int maxChars, int page, int totalPages)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw.Length <= maxChars) return raw;

            if (page <= 1)
            {
                int headBudget = (int)(maxChars * 0.58);
                int tailBudget = Math.Max(60, maxChars - headBudget - 55);
                string head = raw.Substring(0, Math.Min(raw.Length, headBudget));
                string tail = raw.Length > headBudget
                    ? raw.Substring(Math.Max(headBudget, raw.Length - tailBudget))
                    : "";
                return head + "\n...[paginado: " + raw.Length + " chars, " + (lines != null ? lines.Length : 0) + " linhas]...\n" + tail;
            }

            int startOffset = Math.Min(raw.Length - 1, (page - 1) * maxChars);
            int len = Math.Min(maxChars, raw.Length - startOffset);
            return raw.Substring(startOffset, len);
        }

        private static string GetString(Dictionary<string, object> map, string key, string fallback = "")
        {
            if (map == null || !map.ContainsKey(key) || map[key] == null) return fallback;
            return map[key].ToString();
        }

        private static string TruncatePlain(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text.Substring(0, Math.Max(0, max - 3)) + "...";
        }

        public static string BuildRelevanceContextBlock(FridaDevelopmentTaskState taskState, FridaExecutionLedger ledger, int maxChars = 2400)
        {
            if (taskState == null && ledger == null) return "";
            var sb = new StringBuilder();
            sb.AppendLine("[ESTADO_TAREFA_DESENVOLVIMENTO]");
            if (taskState != null)
            {
                sb.AppendLine("task_id=" + taskState.TaskId + " | revisao=" + taskState.ProjectRevision + " | stack=" + (string.IsNullOrEmpty(taskState.DetectedStack) ? "auto" : taskState.DetectedStack));
                sb.AppendLine("status_entregaveis: file_saved=" + taskState.FileSaved
                    + " | build_passed=" + taskState.IsCompilationValidForCurrentRevision
                    + " | tests_passed=" + taskState.IsTestsValidForCurrentRevision
                    + " | request_fulfilled=" + taskState.RequestFulfilled);
                sb.AppendLine("orcamento: ciclos_correcao=" + taskState.CorrectionCyclesUsed + "/" + taskState.MaxCorrectionCycles
                    + " | chamadas_ferramenta=" + taskState.ToolCallsUsed + "/" + taskState.MaxToolCalls);

                if (taskState.TrackedFiles.Count > 0)
                {
                    sb.AppendLine("arquivos_modificados:");
                    foreach (var f in taskState.TrackedFiles.Skip(Math.Max(0, taskState.TrackedFiles.Count - 6)))
                    {
                        sb.AppendLine("  - " + f.FilePath + " (sha256=" + ShortHash(f.LastAgentSha256) + ")");
                    }
                }

                var openFindings = taskState.ReviewFindings.Where(r => r != null && !r.Resolved).ToList();
                if (openFindings.Count > 0)
                {
                    sb.AppendLine("achados_revisor_abertos:");
                    foreach (var finding in openFindings.Take(5))
                    {
                        sb.AppendLine("  - [" + finding.FindingId + "] " + finding.Description);
                    }
                }
            }

            if (ledger != null)
            {
                var unresolved = ledger.Receipts.Where(r => r != null && r.Status == FridaResultStatus.Failed && r.SupersededBy == null).ToList();
                if (unresolved.Count > 0)
                {
                    sb.AppendLine("falhas_ativas_para_corrigir:");
                    foreach (var fail in unresolved.Skip(Math.Max(0, unresolved.Count - 4)))
                    {
                        sb.AppendLine("  - " + fail.Tool + "." + fail.Operation + " (" + fail.OperationId + "): " + TruncatePlain(fail.Summary, 180));
                    }
                }
            }

            string result = sb.ToString().Trim();
            return result.Length <= maxChars ? result : result.Substring(0, maxChars - 3) + "...";
        }

        private static string ShortHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "none";
            return hash.Length <= 10 ? hash : hash.Substring(0, 10);
        }
    }

    public static class FridaCodingTools
    {
        private static readonly string[] IgnoredDirectories = new[]
        {
            ".git", "bin", "obj", "node_modules", "__pycache__", ".vs", ".idea", "packages", "TestResults"
        };

        public static bool IsCompilationCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            return Regex.IsMatch(command, @"(?i)(\bcsc(\.exe)?\b|\bdotnet\s+build\b|\bpy_compile\b|\bcompileall\b|\bgcc\b|\bg\+\+\b|\bjavac\b|\btsc\b|\[System\.Management\.Automation\.Language\.Parser\]::ParseFile)");
        }

        public static bool IsTestExecutionCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            return Regex.IsMatch(command, @"(?i)(\bpytest\b|\bunittest\b|\bdotnet\s+test\b|\bpest\b|\bpester\b|FunctionalTests\.exe|run_tests\.ps1|\btest[s_]|\bassert\b)");
        }

        public static bool IsValidationCommand(string command)
        {
            return IsCompilationCommand(command) || IsTestExecutionCommand(command);
        }

        public static FridaToolReceipt InspectProject(string path, int maxFiles, FridaDevelopmentTaskState taskState = null)
        {
            FridaWorkContext.ThrowIfCancelled();
            var receipt = new FridaToolReceipt("inspect_project", "inspect", path ?? "", false);
            try
            {
                string root = string.IsNullOrWhiteSpace(path) ? Environment.CurrentDirectory : Path.GetFullPath(path);
                if (File.Exists(root))
                    root = Path.GetDirectoryName(root) ?? Environment.CurrentDirectory;
                receipt.Target = root;

                if (!Directory.Exists(root))
                    return receipt.Fail("project_root_not_found", "Diretorio do projeto nao encontrado: " + root);

                int limit = Math.Max(10, Math.Min(maxFiles <= 0 ? 80 : maxFiles, 250));
                var allFiles = EnumerateProjectFiles(root, limit * 2).ToList();

                int csCount = allFiles.Count(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
                int pyCount = allFiles.Count(f => f.EndsWith(".py", StringComparison.OrdinalIgnoreCase));
                int psCount = allFiles.Count(f => f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase));
                int jsTsCount = allFiles.Count(f => f.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase));

                string stack = "generic";
                if (csCount > 0 && pyCount > 0) stack = "mixed_csharp_python";
                else if (csCount > 0) stack = "csharp_net48_csc";
                else if (pyCount > 0) stack = "python";
                else if (psCount > 0) stack = "powershell";
                else if (jsTsCount > 0) stack = "node";

                var envInventory = FridaEnvironmentCapabilities.GetMachineInventory();
                bool hasDotnetSdk = envInventory.ContainsKey("dotnet_cli_sdk_available") && (bool)envInventory["dotnet_cli_sdk_available"];
                string buildRecommendation = "";
                if (csCount > 0)
                {
                    buildRecommendation = hasDotnetSdk
                        ? "dotnet build (ou csc.exe em " + FridaEnvironmentCapabilities.DefaultFrameworkCscPath + ")"
                        : "Use csc.exe diretamente (" + FridaEnvironmentCapabilities.DefaultFrameworkCscPath + "); SDK dotnet CLI ausente nesta maquina.";
                }
                else if (pyCount > 0)
                {
                    buildRecommendation = "python -m py_compile <arquivo.py> e python -m unittest / pytest";
                }
                else if (psCount > 0)
                {
                    buildRecommendation = "powershell -ExecutionPolicy Bypass -File <script.ps1>";
                }

                var fileEntries = new List<Dictionary<string, object>>();
                foreach (var file in allFiles.Take(limit))
                {
                    var fi = new FileInfo(file);
                    fileEntries.Add(new Dictionary<string, object>
                    {
                        { "relative_path", GetRelativePathSafe(root, file) },
                        { "size_bytes", fi.Length },
                        { "sha256", FridaVerifiedTools.FileSha256(file) },
                        { "last_write_utc", fi.LastWriteTimeUtc.ToString("o") }
                    });
                }

                if (taskState != null)
                {
                    taskState.ProjectRoot = root;
                    taskState.DetectedStack = stack;
                    receipt.ProjectRevision = taskState.ProjectRevision;
                }

                receipt.Put("project_root", root);
                receipt.Put("detected_stack", stack);
                receipt.Put("total_files_scanned", allFiles.Count);
                receipt.Put("recommended_build_test", buildRecommendation);
                receipt.Put("machine_capabilities", envInventory);
                receipt.Put("files", fileEntries);
                receipt.Check("project_root_exists", true, "true", "true", "filesystem");
                receipt.Check("capabilities_probed", true, "probed", stack, "environment_probe");

                var js = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                return receipt.Finish(
                    FridaResultStatus.Verified,
                    "project_inspected",
                    "Projeto inspecionado em " + root + " (stack=" + stack + ", arquivos=" + allFiles.Count + "). " + buildRecommendation,
                    js.Serialize(new { project_root = root, detected_stack = stack, recommended_build_test = buildRecommendation, files = fileEntries }));
            }
            catch (Exception ex)
            {
                return receipt.Fail("inspect_project_exception", "Falha ao inspecionar projeto: " + ex.Message);
            }
        }

        public static FridaToolReceipt ReadCodeLines(string path, int startLine, int endLine, FridaDevelopmentTaskState taskState = null)
        {
            FridaWorkContext.ThrowIfCancelled();
            var receipt = new FridaToolReceipt("read_code", "read_lines", path ?? "", false);
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return receipt.Fail("empty_path", "Informe o caminho explicito do arquivo.");
                string fullPath = Path.GetFullPath(path);
                receipt.Target = fullPath;
                if (!File.Exists(fullPath))
                    return receipt.Fail("file_not_found", "Arquivo nao encontrado: " + fullPath);

                string sha256 = FridaVerifiedTools.FileSha256(fullPath);
                if (taskState != null)
                    receipt.ProjectRevision = taskState.ProjectRevision;

                string[] allLines = File.ReadAllLines(fullPath, Encoding.UTF8);
                int totalLines = allLines.Length;
                int start = Math.Max(1, startLine <= 0 ? 1 : startLine);
                int end = endLine <= 0 ? Math.Min(totalLines, start + 199) : Math.Min(totalLines, Math.Max(start, endLine));
                if (end - start > 400)
                    end = start + 400;

                var sb = new StringBuilder();
                for (int i = start; i <= end && i <= totalLines; i++)
                {
                    sb.Append(i).Append(": ").AppendLine(allLines[i - 1]);
                }

                string afterSha = FridaVerifiedTools.FileSha256(fullPath);
                receipt.Put("path", fullPath);
                receipt.Put("sha256", afterSha);
                receipt.Put("start_line", start);
                receipt.Put("end_line", end);
                receipt.Put("total_lines", totalLines);
                receipt.Put("has_more", end < totalLines);
                receipt.Put("next_start_line", end < totalLines ? end + 1 : totalLines);
                receipt.Check("file_exists", true, "true", "true", "filesystem");
                receipt.Check("stable_read", sha256 == afterSha, sha256, afterSha, "sha256_before_after_read");

                return receipt.Finish(
                    FridaResultStatus.Verified,
                    "code_read",
                    "Leitura de " + Path.GetFileName(fullPath) + " linhas " + start + " a " + end + " (de " + totalLines + ", sha256=" + afterSha.Substring(0, 10) + ").",
                    sb.ToString());
            }
            catch (Exception ex)
            {
                return receipt.Fail("read_code_exception", "Erro ao ler codigo: " + ex.Message);
            }
        }

        public static FridaToolReceipt SearchSymbols(string rootPath, string query, string filePattern, int maxMatches, FridaDevelopmentTaskState taskState = null)
        {
            FridaWorkContext.ThrowIfCancelled();
            var receipt = new FridaToolReceipt("search_symbols", "search", rootPath ?? "", false);
            try
            {
                if (string.IsNullOrWhiteSpace(query))
                    return receipt.Fail("empty_query", "Informe o simbolo ou expressao a buscar.");

                string root = string.IsNullOrWhiteSpace(rootPath) ? Environment.CurrentDirectory : Path.GetFullPath(rootPath);
                if (File.Exists(root))
                    root = Path.GetDirectoryName(root) ?? Environment.CurrentDirectory;
                receipt.Target = root;
                if (!Directory.Exists(root))
                    return receipt.Fail("root_not_found", "Diretorio nao encontrado: " + root);

                int limit = Math.Max(5, Math.Min(maxMatches <= 0 ? 40 : maxMatches, 120));
                Regex regex;
                try
                {
                    regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                }
                catch
                {
                    regex = new Regex(Regex.Escape(query), RegexOptions.IgnoreCase | RegexOptions.Compiled);
                }

                var matches = new List<Dictionary<string, object>>();
                var sb = new StringBuilder();
                foreach (var file in EnumerateProjectFiles(root, 400))
                {
                    FridaWorkContext.ThrowIfCancelled();
                    if (!MatchesGlobPattern(file, filePattern))
                        continue;

                    string[] lines;
                    try { lines = File.ReadAllLines(file, Encoding.UTF8); }
                    catch { continue; }

                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            string rel = GetRelativePathSafe(root, file);
                            int lineNum = i + 1;
                            string snippet = lines[i].Trim();
                            if (snippet.Length > 180) snippet = snippet.Substring(0, 180) + "...";

                            matches.Add(new Dictionary<string, object>
                            {
                                { "file", rel },
                                { "full_path", file },
                                { "line", lineNum },
                                { "snippet", snippet }
                            });
                            sb.Append(rel).Append(":").Append(lineNum).Append(": ").AppendLine(snippet);
                            if (matches.Count >= limit) break;
                        }
                    }
                    if (matches.Count >= limit) break;
                }

                if (taskState != null)
                    receipt.ProjectRevision = taskState.ProjectRevision;

                receipt.Put("query", query);
                receipt.Put("root", root);
                receipt.Put("match_count", matches.Count);
                receipt.Put("matches", matches);
                receipt.Check("search_completed", true, "completed", matches.Count + " ocorrencias", "symbol_scanner");

                return receipt.Finish(
                    FridaResultStatus.Verified,
                    "symbols_searched",
                    "Busca por '" + query + "' encontrou " + matches.Count + " ocorrencia(s).",
                    sb.ToString());
            }
            catch (Exception ex)
            {
                return receipt.Fail("search_symbols_exception", "Falha ao buscar simbolos: " + ex.Message);
            }
        }

        public static FridaToolReceipt ApplySurgicalPatch(
            string path,
            string targetText,
            string replacementText,
            string expectedBeforeSha256,
            FridaDevelopmentTaskState taskState = null)
        {
            FridaWorkContext.ThrowIfCancelled();
            var receipt = new FridaToolReceipt("apply_patch", "patch", path ?? "", true);
            string temporary = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return receipt.Fail("empty_path", "Caminho do arquivo vazio.");
                if (string.IsNullOrEmpty(targetText))
                    return receipt.Fail("empty_target", "Bloco target_text obrigatorio para aplicar patch com seguranca.");

                string fullPath = Path.GetFullPath(path);
                receipt.Target = fullPath;
                if (!File.Exists(fullPath))
                    return receipt.Fail("file_not_found", "Arquivo nao encontrado para patch: " + fullPath);

                string currentSha = FridaVerifiedTools.FileSha256(fullPath);
                receipt.Put("before_sha256", currentSha);

                // Operator / concurrent modification conflict detection
                if (!string.IsNullOrWhiteSpace(expectedBeforeSha256)
                    && !string.Equals(currentSha, expectedBeforeSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    receipt.Check("version_match", false, expectedBeforeSha256.Trim(), currentSha, "sha256_precheck");
                    return receipt.Fail("operator_conflict", "Conflito de versao detectado em " + Path.GetFileName(fullPath) + ": o arquivo foi modificado externamente (sha256 atual=" + currentSha.Substring(0, 10) + "). Releia o arquivo antes de aplicar o patch.");
                }

                if (taskState != null)
                {
                    var tracked = taskState.TrackedFiles.FirstOrDefault(f => string.Equals(f.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
                    if (tracked != null && !string.IsNullOrEmpty(tracked.LastAgentSha256)
                        && !string.Equals(currentSha, tracked.LastAgentSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        receipt.Check("operator_modification_preserved", false, tracked.LastAgentSha256, currentSha, "sha256_precheck");
                        return receipt.Fail("operator_conflict", "Modificacao externa do operador detectada em " + Path.GetFileName(fullPath) + ". Releia o arquivo para preservar as alteracoes locais antes de editar.");
                    }
                }

                byte[] rawBytes = File.ReadAllBytes(fullPath);
                bool hasUtf8Bom = rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF;
                string originalContent = File.ReadAllText(fullPath, Encoding.UTF8);

                string updatedContent;
                if (originalContent.Contains(targetText))
                {
                    updatedContent = originalContent.Replace(targetText, replacementText ?? "");
                }
                else
                {
                    string normOrig = originalContent.Replace("\r\n", "\n");
                    string normTarget = targetText.Replace("\r\n", "\n");
                    if (!normOrig.Contains(normTarget))
                    {
                        receipt.Check("target_found", false, "target_text presente", "ausente", "patch_matcher");
                        return receipt.Fail("patch_target_not_found", "Trecho target_text nao encontrado em " + Path.GetFileName(fullPath) + ". Use read_code para conferir as linhas exatas.");
                    }
                    string lineEnding = originalContent.Contains("\r\n") ? "\r\n" : "\n";
                    string normRepl = (replacementText ?? "").Replace("\r\n", "\n");
                    updatedContent = normOrig.Replace(normTarget, normRepl);
                    if (lineEnding == "\r\n")
                        updatedContent = updatedContent.Replace("\n", "\r\n");
                }

                var encoding = new UTF8Encoding(hasUtf8Bom);
                byte[] updatedBytes = encoding.GetBytes(updatedContent);
                temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(updatedBytes, 0, updatedBytes.Length);
                    output.Flush(true);
                }

                string expectedSha = FridaVerifiedTools.FileSha256(temporary);
                if (currentSha != FridaVerifiedTools.FileSha256(fullPath))
                    return receipt.Fail("concurrent_change", "O arquivo mudou durante a preparacao do patch. Substituicao abortada para preservar dados.");

                File.Replace(temporary, fullPath, null);
                string afterSha = FridaVerifiedTools.FileSha256(fullPath);

                string diff = GenerateCompactDiff(Path.GetFileName(fullPath), targetText, replacementText ?? "");
                if (taskState != null)
                {
                    taskState.RecordFileModification(fullPath, currentSha, afterSha, diff, receipt.OperationId);
                    receipt.ProjectRevision = taskState.ProjectRevision;
                }

                receipt.MonitorKind = "file";
                receipt.MonitorHash = afterSha;
                receipt.Put("path", fullPath);
                receipt.Put("after_sha256", afterSha);
                receipt.Put("sha256", afterSha);
                receipt.Put("diff", diff);
                receipt.Check("version_match", true, currentSha, currentSha, "sha256_precheck");
                receipt.Check("file_bytes_sha256", expectedSha == afterSha, expectedSha, afterSha, "sha256_readback");
                receipt.Check("file_exists", File.Exists(fullPath), "true", File.Exists(fullPath).ToString(), "filesystem");

                return receipt.Finish(
                    FridaResultStatus.Verified,
                    "file_saved",
                    "Patch aplicado e verificado por SHA-256 em " + Path.GetFileName(fullPath) + ".",
                    diff);
            }
            catch (Exception ex)
            {
                return receipt.Fail("apply_patch_exception", "Erro ao aplicar patch: " + ex.Message);
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                {
                    try { File.Delete(temporary); } catch { }
                }
            }
        }

        public static FridaToolReceipt RunProjectValidation(
            string workingDirectory,
            string command,
            string validationKind,
            int timeoutSeconds,
            FridaDevelopmentTaskState taskState = null)
        {
            string kind = string.IsNullOrWhiteSpace(validationKind) ? "test" : validationKind.Trim().ToLowerInvariant();
            var receipt = new FridaToolReceipt("run_project_validation", kind, command ?? "", true);
            var cancellation = FridaWorkContext.Token;

            if (taskState != null)
                receipt.ProjectRevision = taskState.ProjectRevision;

            if (cancellation.IsCancellationRequested)
                return receipt.Finish(FridaResultStatus.Unconfirmed, "user_cancelled", "Validacao cancelada antes de iniciar.");

            if (string.IsNullOrWhiteSpace(command))
                return receipt.Fail("empty_command", "Comando de validacao vazio.");

            string workDir = string.IsNullOrWhiteSpace(workingDirectory)
                ? (taskState != null && !string.IsNullOrWhiteSpace(taskState.ProjectRoot) ? taskState.ProjectRoot : Environment.CurrentDirectory)
                : FridaSshWorkspace.IsRemote ? FridaSshWorkspace.ResolvePath(workingDirectory) : Path.GetFullPath(workingDirectory);

            if (!FridaSshWorkspace.IsRemote && !Directory.Exists(workDir))
                return receipt.Fail("working_directory_not_found", "Diretorio de trabalho inexistente: " + workDir);

            // Report absence of dotnet CLI SDK accurately instead of failing mysteriously
            var env = FridaEnvironmentCapabilities.GetMachineInventory();
            bool hasDotnet = env.ContainsKey("dotnet_cli_sdk_available") && (bool)env["dotnet_cli_sdk_available"];
            if (!FridaSshWorkspace.IsRemote && !hasDotnet && Regex.IsMatch(command.Trim(), @"^dotnet\s+(build|test|run|new)\b", RegexOptions.IgnoreCase))
            {
                receipt.Check("dotnet_sdk_available", false, "SDK dotnet instalado", "AUSENTE (apenas Runtime instalado)", "environment_probe");
                return receipt.Fail(
                    "missing_dependency",
                    "O comando '" + command + "' requer o SDK 'dotnet', que nao esta instalado nesta maquina. Utilize o compilador C# nativo instalado em '" + FridaEnvironmentCapabilities.DefaultFrameworkCscPath + "' e execute o binario resultante.");
            }

            int timeoutMs = Math.Max(3, Math.Min(timeoutSeconds <= 0 ? 45 : timeoutSeconds, 180)) * 1000;
            try
            {
                string script = FridaSshWorkspace.IsRemote
                    ? "cd " + FridaSshWorkspace.ShellQuote(workDir) + " || exit 97\n" + command
                    : "Set-Location -LiteralPath '" + workDir.Replace("'", "''") + "'; " + command;
                var execution = FridaVerifiedTools.RunPowerShell(script, timeoutMs);
                receipt.Put("working_directory", workDir);
                receipt.Put("validation_kind", kind);
                receipt.AddValidationEvidenceRef(execution.OperationId);
                foreach (var pair in execution.Data) receipt.Put(pair.Key, pair.Value);
                if (execution.Status == FridaResultStatus.Unconfirmed)
                {
                    if (taskState != null)
                    {
                        taskState.UncertainOperationsPendingReconciliation.Add(receipt.OperationId);
                        if (execution.Scope != "user_cancelled") taskState.RecordValidation(receipt.StepId, receipt.OperationId, kind, false, execution.Summary);
                    }
                    if (execution.Scope == "process_timeout") receipt.Put("timed_out", true);
                    return receipt.Finish(FridaResultStatus.Unconfirmed, execution.Scope, execution.Summary, execution.Output);
                }
                bool passed = execution.Status == FridaResultStatus.ProcessCompleted;
                object exit;
                string exitCode = execution.Data.TryGetValue("exit_code", out exit) ? Convert.ToString(exit) : "indisponível";
                receipt.Check("process_exit_code", passed, "0", exitCode, FridaSshWorkspace.IsRemote ? "remote_process_exit" : "process_handle");
                bool both = passed && IsCompilationCommand(command) && IsTestExecutionCommand(command);
                if (taskState != null)
                {
                    if (both)
                    {
                        taskState.RecordValidation(receipt.StepId, receipt.OperationId, "compile", true, execution.Summary);
                        taskState.RecordValidation(receipt.StepId, receipt.OperationId, "test", true, execution.Summary);
                    }
                    else taskState.RecordValidation(receipt.StepId, receipt.OperationId, kind, passed, execution.Summary);
                }
                bool compile = kind == "compile" || kind == "build";
                if (!passed)
                {
                    receipt.ErrorCode = compile ? "compilation_failed" : "test_failed";
                    return receipt.Finish(FridaResultStatus.Failed, compile ? "build_failed" : "tests_failed", "Validação falhou na revisão " + receipt.ProjectRevision + ".", execution.Output);
                }
                string scope = both ? "tests_passed_with_compile" : compile ? "build_passed" : kind == "test" || kind == "tests" ? "tests_passed" : "process_completed";
                return receipt.Finish(FridaResultStatus.Verified, scope, "Validação (" + scope + ") aprovada na revisão " + receipt.ProjectRevision + ".", execution.Output);
            }
            catch (Exception ex) { return receipt.Fail("validation_exception", "Falha ao executar validação: " + ex.Message); }
        }

        public static string GenerateCompactDiff(string fileName, string beforeText, string afterText)
        {
            var sb = new StringBuilder();
            sb.AppendLine("--- a/" + (fileName ?? "file"));
            sb.AppendLine("+++ b/" + (fileName ?? "file"));
            string[] oldLines = (beforeText ?? "").Replace("\r\n", "\n").Split('\n');
            string[] newLines = (afterText ?? "").Replace("\r\n", "\n").Split('\n');
            int maxOld = Math.Min(oldLines.Length, 25);
            int maxNew = Math.Min(newLines.Length, 25);
            for (int i = 0; i < maxOld; i++)
                sb.AppendLine("- " + oldLines[i]);
            if (oldLines.Length > maxOld)
                sb.AppendLine("- ... (" + (oldLines.Length - maxOld) + " linhas omitidas)");
            for (int i = 0; i < maxNew; i++)
                sb.AppendLine("+ " + newLines[i]);
            if (newLines.Length > maxNew)
                sb.AppendLine("+ ... (" + (newLines.Length - maxNew) + " linhas omitidas)");
            return sb.ToString().Trim();
        }

        private static IEnumerable<string> EnumerateProjectFiles(string root, int maxCount)
        {
            var results = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0 && results.Count < maxCount)
            {
                string current = stack.Pop();
                try
                {
                    foreach (var file in Directory.GetFiles(current))
                    {
                        results.Add(file);
                        if (results.Count >= maxCount) break;
                    }
                    foreach (var dir in Directory.GetDirectories(current))
                    {
                        string name = Path.GetFileName(dir);
                        if (IgnoredDirectories.Any(ig => string.Equals(ig, name, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        stack.Push(dir);
                    }
                }
                catch { }
            }
            return results;
        }

        private static bool MatchesGlobPattern(string filePath, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Trim() == "*" || pattern.Trim() == "*.*")
                return true;
            string fileName = Path.GetFileName(filePath) ?? "";
            foreach (var part in pattern.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                if (p.StartsWith("*."))
                {
                    string ext = p.Substring(1);
                    if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (fileName.IndexOf(p.Replace("*", ""), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string GetRelativePathSafe(string root, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(fullPath))
                return fullPath ?? "";
            string normRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(normRoot.Length);
            return fullPath;
        }
    }
}
