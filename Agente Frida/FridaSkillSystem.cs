// Sistema de Atualizacao e Aprendizado Dinamico de Skills da Frida
// Gerenciado pelo Gestor (DeepSeek V4 Pro) com base em analise de complexidade,
// descoberta pelo Estagiario, implementacao pelo Tecnico e verificacao pelo Analista.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public enum FridaSkillComplexity
    {
        Low,      // Script simples, comando CLI unico ou automacao local direta
        Medium,   // Integracao de formatos, APIs externas, utilitarios de terceiros
        High,     // Arquitetura multi-etapas, automacao COM (SolidWorks/AutoCAD), redes
        Extreme   // Engenharia reversa de binarios, DLL hooking, emuladores e drivers
    }

    public sealed class FridaSkillEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public FridaSkillComplexity Complexity { get; set; }
        public List<string> TriggerPatterns { get; set; }
        public List<string> TriggerKeywords { get { return TriggerPatterns; } set { TriggerPatterns = value; } }
        public List<string> AssignedAgents { get; set; }
        public string ExecutionKind { get; set; } // powershell, python, cli, workflow
        public string ScriptFileName { get; set; }
        public string ScriptContent { get; set; }
        public string VerificationCommand { get; set; }
        public bool Verified { get; set; }
        public string VerificationProof { get; set; }
        public string VerificationSha256 { get; set; }
        public string SkillDirectory { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime LastUsedUtc { get; set; }
        public int ExecutionCount { get; set; }
        public int SuccessCount { get; set; }
        public string Instructions { get; set; }
        public bool IsInstructional
        {
            get
            {
                return ExecutionKind == "instructional"
                    || (string.IsNullOrWhiteSpace(ScriptFileName) && string.IsNullOrWhiteSpace(ScriptContent))
                    || (!string.IsNullOrWhiteSpace(ExecutionKind) && ExecutionKind.IndexOf("instruct", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }
        // Lessons from approved team runs ("dd/MM/yyyy — lição"), each batch raising the version.
        public List<string> Lessons { get; set; }
        public int Version { get; set; }

        public FridaSkillEntry()
        {
            TriggerPatterns = new List<string>();
            AssignedAgents = new List<string>();
            Lessons = new List<string>();
            Version = 1;
            CreatedUtc = DateTime.UtcNow;
            LastUsedUtc = DateTime.UtcNow;
            ExecutionKind = "powershell";
            ScriptFileName = "run.ps1";
        }
    }

    public static class FridaSkillRegistry
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, FridaSkillEntry> _skills = new Dictionary<string, FridaSkillEntry>(StringComparer.OrdinalIgnoreCase);
        private static string _baseDir;
        private static string _skillsDir;
        private static string _registryFile;
        private static bool _initialized = false;
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        public static void Initialize(string baseDir = null)
        {
            lock (Sync)
            {
                if (_initialized && string.IsNullOrEmpty(baseDir)) return;
                _baseDir = baseDir ?? AppDomain.CurrentDomain.BaseDirectory;
                _skillsDir = Path.Combine(_baseDir, "skills");
                _registryFile = Path.Combine(_skillsDir, "frida_skills_registry.json");

                Directory.CreateDirectory(_skillsDir);
                _skills.Clear();

                // 1. Carrega o registro mestre se existir
                if (File.Exists(_registryFile))
                {
                    try
                    {
                        var list = _json.Deserialize<List<FridaSkillEntry>>(NormalizeRegistryJson(File.ReadAllText(_registryFile, Encoding.UTF8)));
                        if (list != null)
                        {
                            foreach (var s in list)
                            {
                                if (s == null || string.IsNullOrWhiteSpace(s.Id)) continue;
                                // A catalog copied or restored elsewhere still points at its old folder.
                                string local = Path.Combine(_skillsDir, s.Id);
                                if ((string.IsNullOrWhiteSpace(s.SkillDirectory) || !Directory.Exists(s.SkillDirectory)) && Directory.Exists(local)) s.SkillDirectory = local;
                                _skills[s.Id] = s;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        BootLogger.Log("Falha ao ler frida_skills_registry.json: " + ex.Message);
                        // Kept aside before anything rewrites the catalog, so a hand-edited file is never lost.
                        try { File.Copy(_registryFile, _registryFile + ".ilegivel-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), false); }
                        catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }

                // 2. Varre subpastas por SKILL.md para reidratar skills adicionadas manualmente
                try
                {
                    foreach (string dir in Directory.GetDirectories(_skillsDir))
                    {
                        string mdFile = Path.Combine(dir, "SKILL.md");
                        string id = Path.GetFileName(dir).ToLowerInvariant();
                        if (File.Exists(mdFile) && !_skills.ContainsKey(id))
                        {
                            var s = ParseSkillMarkdown(id, mdFile, dir);
                            if (s != null) _skills[id] = s;
                        }
                    }
                }
                catch { }

                _initialized = true;
                BootLogger.Log("FridaSkillRegistry inicializado com " + _skills.Count + " skill(s) carregada(s).");
            }
        }

        // The serializer writes dates as "\/Date(ms)\/". A catalog edited or reformatted by hand usually
        // loses the backslashes, and one such date used to make the whole catalog unreadable.
        internal static string NormalizeRegistryJson(string raw)
        {
            return Regex.Replace(raw ?? "", @"""/Date\((-?\d+)\)/""", @"""\/Date($1)\/""");
        }

        internal const string GeneratedMarker ="<!-- frida:skill-gerada -->";

        private static List<string> MarkdownBullets(string text, string headingPattern)
        {
            var items = new List<string>();
            Match section = Regex.Match(text, @"(?im)^##?\s+" + headingPattern + @"\s*\r?\n([\s\S]*?)(?=^##?\s|\z)");
            if (!section.Success) return items;
            foreach (Match bullet in Regex.Matches(section.Groups[1].Value, @"(?m)^\s*[-*]\s+(.+?)\s*$"))
            {
                string item = bullet.Groups[1].Value.Trim().Trim('`').Trim();
                if (item.Length > 0) items.Add(item);
            }
            return items;
        }

        private static FridaSkillEntry ParseSkillMarkdown(string id, string mdPath, string dir)
        {
            try
            {
                string text = File.ReadAllText(mdPath, Encoding.UTF8);
                var entry = new FridaSkillEntry
                {
                    Id = id,
                    Name = id,
                    SkillDirectory = dir,
                    Description = "Skill carregada de " + Path.GetFileName(mdPath)
                };

                Match nameM = Regex.Match(text, @"^#\s+(.+)$", RegexOptions.Multiline);
                if (nameM.Success) entry.Name = nameM.Groups[1].Value.Trim();

                Match descM = Regex.Match(text, @"(?i)##?\s+Descri[çc][ãa]o\s*\r?\n+(.+)", RegexOptions.Multiline);
                if (descM.Success) entry.Description = descM.Groups[1].Value.Trim();

                Match cmdM = Regex.Match(text, @"(?i)##?\s+Comando de Verifica[çc][ãa]o\s*\r?\n+```(?:powershell|cmd|sh)?\r?\n([\s\S]*?)```");
                if (cmdM.Success) entry.VerificationCommand = cmdM.Groups[1].Value.Trim();

                // The script named in the document comes first, then the usual names.
                var candidates = new List<string>();
                Match mode = Regex.Match(text, @"(?i)Modo de Execu[çc][ãa]o\*{0,2}\s*:\s*`?\w+`?\s*\(\s*`?([^`)\s]+)`?\s*\)");
                if (mode.Success && Path.GetFileName(mode.Groups[1].Value) == mode.Groups[1].Value) candidates.Add(mode.Groups[1].Value);
                candidates.AddRange(new[] { "run.ps1", "run.py", "handler.py", "handler.ps1" });
                string script = candidates.FirstOrDefault(name => File.Exists(Path.Combine(dir, name)));
                if (script != null)
                {
                    entry.ScriptFileName = script;
                    entry.ExecutionKind = script.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ? "python" : "powershell";
                    entry.ScriptContent = File.ReadAllText(Path.Combine(dir, script), Encoding.UTF8);
                }

                entry.TriggerPatterns = MarkdownBullets(text, @"Padr[õo]es de Ativa[çc][ãa]o");
                entry.Lessons = MarkdownBullets(text, @"Li[çc][õo]es aprendidas");
                Match version = Regex.Match(text, @"(?i)Vers[ãa]o\*{0,2}\s*:\s*(\d+)");
                int parsedVersion;
                if (version.Success && int.TryParse(version.Groups[1].Value, out parsedVersion)) entry.Version = Math.Max(1, parsedVersion);
                Match complexity = Regex.Match(text, @"(?i)Complexidade\*{0,2}\s*:\s*`?(\w+)`?");
                FridaSkillComplexity parsedComplexity;
                if (complexity.Success && Enum.TryParse(complexity.Groups[1].Value, true, out parsedComplexity)) entry.Complexity = parsedComplexity;

                // The document's own "verified" line is only text. The state comes from the receipt the
                // verification wrote; without it the skill returns as a draft until it is verified again.
                string receipt = Path.Combine(dir, "verification.json");
                if (File.Exists(receipt))
                {
                    try
                    {
                        var proof = _json.Deserialize<Dictionary<string, object>>(File.ReadAllText(receipt, Encoding.UTF8));
                        object value;
                        if (proof != null && proof.TryGetValue("verified", out value) && value is bool && (bool)value)
                        {
                            entry.Verified = true;
                            if (proof.TryGetValue("proof", out value)) entry.VerificationProof = Convert.ToString(value);
                            if (proof.TryGetValue("sha256", out value)) entry.VerificationSha256 = Convert.ToString(value);
                        }
                    }
                    catch (ArgumentException) { } catch (InvalidOperationException) { }
                }

                return entry;
            }
            catch { return null; }
        }

        public static List<FridaSkillEntry> GetAll()
        {
            lock (Sync) { return _skills.Values.OrderBy(s => s.Name).ToList(); }
        }

        public static List<FridaSkillEntry> ListSkills()
        {
            return GetAll();
        }

        public static FridaSkillEntry Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            lock (Sync)
            {
                FridaSkillEntry entry;
                return _skills.TryGetValue(id.Trim().ToLowerInvariant(), out entry) ? entry : null;
            }
        }

        public static bool Exists(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            lock (Sync) { return _skills.ContainsKey(id.Trim().ToLowerInvariant()); }
        }

        public static FridaSkillEntry FindMatching(string userRequest)
        {
            if (string.IsNullOrWhiteSpace(userRequest)) return null;
            string req = userRequest.ToLowerInvariant();
            lock (Sync)
            {
                foreach (var skill in _skills.Values)
                {
                    if (skill.TriggerPatterns != null)
                    {
                        foreach (string pattern in skill.TriggerPatterns)
                        {
                            if (string.IsNullOrWhiteSpace(pattern)) continue;
                            if (req.Contains(pattern.ToLowerInvariant())) return skill;
                            try
                            {
                                if (Regex.IsMatch(userRequest, pattern, RegexOptions.IgnoreCase)) return skill;
                            }
                            catch { }
                        }
                    }
                    if (req.Contains(skill.Id) || (!string.IsNullOrWhiteSpace(skill.Name) && req.Contains(skill.Name.ToLowerInvariant())))
                        return skill;
                }
            }
            return null;
        }

        public static FridaToolReceipt RegisterAndSave(FridaSkillEntry skill)
        {
            if (skill == null || string.IsNullOrWhiteSpace(skill.Id))
                return new FridaToolReceipt("manage_skills", "register", "skill", false).Fail("invalid_skill", "Skill nula ou sem identificador valido.");

            string cleanId = Regex.Replace(skill.Id.Trim().ToLowerInvariant(), @"[^a-z0-9_-]", "-").Trim('-');
            skill.Id = cleanId;

            lock (Sync)
            {
                Initialize();
                string targetDir = Path.Combine(_skillsDir, cleanId);
                Directory.CreateDirectory(targetDir);
                skill.SkillDirectory = targetDir;

                // 1. Grava o script executavel (com backup preventivo para rollback)
                string scriptPath = Path.Combine(targetDir, string.IsNullOrWhiteSpace(skill.ScriptFileName) ? "run.ps1" : skill.ScriptFileName);
                if (File.Exists(scriptPath))
                {
                    try { File.Copy(scriptPath, scriptPath + ".previous", true); } catch { }
                }
                if (!string.IsNullOrEmpty(skill.ScriptContent))
                {
                    File.WriteAllText(scriptPath, skill.ScriptContent, new UTF8Encoding(false));
                }

                // 2. Gera o documento formal SKILL.md. A document written by hand (no generator mark) holds
                // instructions the catalog does not have, so it is left as it is.
                string mdPath = Path.Combine(targetDir, "SKILL.md");
                if (!File.Exists(mdPath) || File.ReadAllText(mdPath, Encoding.UTF8).Contains(GeneratedMarker))
                    File.WriteAllText(mdPath, BuildSkillMarkdown(skill), new UTF8Encoding(false));

                // 3. Grava o comprovante de verificacao se aprovado; a stale receipt must not outlive a failed check.
                string receiptPath = Path.Combine(targetDir, "verification.json");
                if (!skill.Verified && File.Exists(receiptPath)) File.Delete(receiptPath);
                if (skill.Verified)
                {
                    string verifyJson = _json.Serialize(new
                    {
                        skill_id = skill.Id,
                        verified = skill.Verified,
                        verified_utc = DateTime.UtcNow.ToString("o"),
                        proof = skill.VerificationProof,
                        sha256 = skill.VerificationSha256,
                        complexity = skill.Complexity.ToString(),
                        assigned_agents = skill.AssignedAgents
                    });
                    File.WriteAllText(receiptPath, verifyJson, new UTF8Encoding(false));
                }

                _skills[cleanId] = skill;

                // 4. Atualiza o catalogo mestre frida_skills_registry.json
                try
                {
                    string regJson = _json.Serialize(_skills.Values.ToList());
                    File.WriteAllText(_registryFile, regJson, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    BootLogger.Log("Falha ao salvar frida_skills_registry.json: " + ex.Message);
                }

                BootLogger.Log("Skill '" + cleanId + "' (" + skill.Name + ") registrada com sucesso [Verified=" + skill.Verified + "].");
            }

            var receipt = new FridaToolReceipt("manage_skills", "register", skill.Id, true);
            receipt.Put("skill_id", skill.Id);
            receipt.Put("name", skill.Name);
            receipt.Put("verified", skill.Verified);
            receipt.Put("directory", skill.SkillDirectory);
            receipt.Check("skill_registered", true, "registered", "registered", "FridaSkillRegistry");
            FridaMeetingWorkspace.Skill(skill);
            return receipt.Finish(FridaResultStatus.Verified, "skill_registered", "Skill '" + skill.Name + "' registrada e integrada ao catalogo da Frida.");
        }

        // Appends what an approved run taught to an existing skill, raises its version, saves SKILL.md
        // and the catalog, then runs the skill's verification again.
        public static FridaToolReceipt AddLessons(string skillId, IEnumerable<string> lessons)
        {
            var skill = Get(skillId);
            if (skill == null)
                return new FridaToolReceipt("manage_skills", "lessons", skillId ?? "", false).Fail("not_found", "Skill '" + skillId + "' nao encontrada.");
            var fresh = (lessons ?? new string[0]).Select(l => (l ?? "").Trim()).Where(l => l.Length > 0).Take(10).ToList();
            if (fresh.Count == 0)
                return new FridaToolReceipt("manage_skills", "lessons", skill.Id, false).Fail("no_lessons", "Nenhuma licao informada.");
            lock (Sync)
            {
                if (skill.Lessons == null) skill.Lessons = new List<string>();
                string stamp = DateTime.Now.ToString("dd/MM/yyyy");
                foreach (string lesson in fresh)
                    if (!skill.Lessons.Any(x => x.EndsWith(lesson, StringComparison.OrdinalIgnoreCase))) skill.Lessons.Add(stamp + " — " + lesson);
                skill.Version = Math.Max(1, skill.Version) + 1;
            }
            var saved = RegisterAndSave(skill);
            return string.IsNullOrWhiteSpace(skill.VerificationCommand) ? saved : VerifySkill(skill.Id);
        }

        public static bool RollbackScript(string skillId)
        {
            var skill = Get(skillId);
            if (skill == null || string.IsNullOrWhiteSpace(skill.SkillDirectory)) return false;
            string scriptPath = Path.Combine(skill.SkillDirectory, string.IsNullOrWhiteSpace(skill.ScriptFileName) ? "run.ps1" : skill.ScriptFileName);
            string backup = scriptPath + ".previous";
            if (File.Exists(backup))
            {
                try
                {
                    File.Copy(backup, scriptPath, true);
                    skill.ScriptContent = File.ReadAllText(scriptPath, Encoding.UTF8);
                    RegisterAndSave(skill);
                    return true;
                }
                catch { return false; }
            }
            return false;
        }

        public static FridaToolReceipt ReadSkill(string skillId)
        {
            var skill = Get(skillId);
            if (skill == null)
                return new FridaToolReceipt("manage_skills", "read", skillId ?? "", false).Fail("not_found", "Skill '" + skillId + "' nao encontrada no catalogo.");
            string mdPath = Path.Combine(skill.SkillDirectory, "SKILL.md");
            string md = File.Exists(mdPath) ? File.ReadAllText(mdPath, Encoding.UTF8) : BuildSkillMarkdown(skill);
            var receipt = new FridaToolReceipt("manage_skills", "read", skill.Id, false);
            receipt.Put("skill_id", skill.Id);
            receipt.Put("name", skill.Name);
            receipt.Put("complexity", skill.Complexity.ToString());
            receipt.Put("execution_kind", skill.ExecutionKind);
            receipt.Put("verified", skill.Verified);
            receipt.Put("is_instructional", skill.IsInstructional);
            receipt.Put("instructions", md);
            if (!string.IsNullOrWhiteSpace(skill.ScriptFileName))
                receipt.Put("script_path", Path.Combine(skill.SkillDirectory, skill.ScriptFileName));
            return receipt.Finish(FridaResultStatus.Verified, "skill_loaded", "Instrucoes da skill '" + skill.Name + "' carregadas sob demanda.", md);
        }

        public static bool IsTrivialVerification(string command)
        {
            string c = (command ?? "").Trim().TrimEnd(';').Trim();
            if (c.Length == 0) return true;
            // One statement that only prints a literal, or only sets the exit code / returns true.
            return Regex.IsMatch(c, @"^(?:write-output|write-host|echo|rem|::)\b[^;|&\r\n]*$", RegexOptions.IgnoreCase)
                || Regex.IsMatch(c, @"^(?:exit(?:\s+0)?|\$true|true|return|cmd\s+/c\s+exit\s+0)$", RegexOptions.IgnoreCase)
                || Regex.IsMatch(c, @"^(['""]).*\1$");
        }

        private static bool HasRunnableScript(FridaSkillEntry skill)
        {
            if (string.IsNullOrWhiteSpace(skill.SkillDirectory) || string.IsNullOrWhiteSpace(skill.ScriptFileName)) return false;
            string path = Path.Combine(skill.SkillDirectory, skill.ScriptFileName);
            if (!File.Exists(path)) return false;
            // Comments and blank lines alone are a placeholder, not a script.
            return File.ReadAllLines(path, Encoding.UTF8).Any(line => { string l = line.Trim(); return l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("//") && !l.StartsWith("rem ", StringComparison.OrdinalIgnoreCase); });
        }

        public static FridaToolReceipt VerifySkill(string skillId)
        {
            var skill = Get(skillId);
            if (skill == null)
                return new FridaToolReceipt("manage_skills", "verify", skillId, false).Fail("not_found", "Skill '" + skillId + "' nao encontrada.");

            if (skill.IsInstructional)
            {
                lock (Sync)
                {
                    skill.Verified = true;
                    skill.VerificationProof = "Skill instrucional validada: procedimentos em conformidade com SKILL.md.";
                    skill.VerificationSha256 = FridaVerifiedTools.Sha256(Encoding.UTF8.GetBytes(skill.Instructions ?? skill.Description ?? ""));
                    RegisterAndSave(skill);
                }
                var instReceipt = new FridaToolReceipt("manage_skills", "verify", skillId, false);
                instReceipt.Put("skill_id", skillId);
                instReceipt.Put("verified", true);
                return instReceipt.Finish(FridaResultStatus.Verified, "instructional_verified", "Procedimento instrucional validado.");
            }

            if (string.IsNullOrWhiteSpace(skill.VerificationCommand))
                return new FridaToolReceipt("manage_skills", "verify", skillId, false).Fail("no_test_command", "A skill nao possui comando de verificacao definido.");

            // A command that only prints or exits proves nothing about the skill, and a script that was
            // never written cannot have been tested. Neither may mark the skill as verified.
            string refusal = IsTrivialVerification(skill.VerificationCommand)
                ? "O comando de verificacao nao testa a skill (apenas imprime ou encerra). Defina um teste que execute o script."
                : !HasRunnableScript(skill) ? "A skill nao tem um script executavel gravado; nao ha o que verificar." : null;
            if (refusal != null)
            {
                lock (Sync) { skill.Verified = false; skill.VerificationProof = "Verificacao recusada: " + refusal; RegisterAndSave(skill); }
                return new FridaToolReceipt("manage_skills", "verify", skillId, false).Fail(IsTrivialVerification(skill.VerificationCommand) ? "trivial_test_command" : "script_missing", refusal);
            }

            BootLogger.Log("Iniciando verificacao da skill '" + skill.Id + "': " + skill.VerificationCommand);
            var result = FridaVerifiedTools.RunPowerShell(skill.VerificationCommand, 45000, skill.SkillDirectory);

            lock (Sync)
            {
                if (result.Status == FridaResultStatus.Verified || result.Status == FridaResultStatus.ProcessCompleted)
                {
                    skill.Verified = true;
                    skill.VerificationProof = result.Output;
                    skill.VerificationSha256 = FridaVerifiedTools.Sha256(Encoding.UTF8.GetBytes(result.Output ?? ""));
                    RegisterAndSave(skill);
                    return result;
                }
                else
                {
                    skill.Verified = false;
                    skill.VerificationProof = "Falha no teste: " + result.Output;
                    RegisterAndSave(skill);
                    return result;
                }
            }
        }

        public static async Task<FridaToolReceipt> ExecuteSkillAsync(string skillId, string argumentsJson)
        {
            var skill = Get(skillId);
            if (skill == null)
                return new FridaToolReceipt("manage_skills", "execute", skillId, false).Fail("not_found", "Skill '" + skillId + "' nao encontrada.");

            if (skill.IsInstructional)
            {
                lock (Sync)
                {
                    skill.ExecutionCount++;
                    skill.LastUsedUtc = DateTime.UtcNow;
                    skill.SuccessCount++;
                    try { File.WriteAllText(_registryFile, _json.Serialize(_skills.Values.ToList()), new UTF8Encoding(false)); } catch { }
                }
                string mdPath = Path.Combine(skill.SkillDirectory, "SKILL.md");
                string content = File.Exists(mdPath) ? File.ReadAllText(mdPath, Encoding.UTF8) : (skill.Instructions ?? BuildSkillMarkdown(skill));
                var instReceipt = new FridaToolReceipt("manage_skills", "execute", skillId, false);
                instReceipt.Put("skill_id", skillId);
                instReceipt.Put("name", skill.Name);
                instReceipt.Put("is_instructional", true);
                instReceipt.Put("instructions", content);
                return instReceipt.Finish(FridaResultStatus.Verified, "instructional_loaded", "Instrucoes da skill '" + skill.Name + "' carregadas para orientar o atendimento.", content);
            }

            string scriptPath = Path.Combine(skill.SkillDirectory, skill.ScriptFileName ?? "run.ps1");
            if (!File.Exists(scriptPath))
                return new FridaToolReceipt("manage_skills", "execute", skillId, false).Fail("script_missing", "Arquivo de execucao '" + skill.ScriptFileName + "' nao encontrado.");

            string cmd;
            if ((skill.ScriptFileName ?? "").EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                cmd = "python \"" + scriptPath + "\" " + (argumentsJson ?? "");
            else
                cmd = "& \"" + scriptPath + "\" " + (argumentsJson ?? "");

            var receipt = await Task.Run(() => FridaVerifiedTools.RunPowerShell(cmd, 60000, skill.SkillDirectory));
            lock (Sync)
            {
                skill.ExecutionCount++;
                skill.LastUsedUtc = DateTime.UtcNow;
                if (receipt.Status == FridaResultStatus.Verified || receipt.Status == FridaResultStatus.ProcessCompleted)
                    skill.SuccessCount++;
                try { File.WriteAllText(_registryFile, _json.Serialize(_skills.Values.ToList()), new UTF8Encoding(false)); } catch { }
            }
            return receipt;
        }

        private static string BuildSkillMarkdown(FridaSkillEntry skill)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# " + skill.Name);
            sb.AppendLine();
            sb.AppendLine("## Descrição");
            sb.AppendLine(skill.Description ?? "Habilidade aprendida dinamicamente pela Frida.");
            sb.AppendLine();
            sb.AppendLine("## Metadados Operacionais");
            sb.AppendLine("- **ID**: `" + skill.Id + "`");
            sb.AppendLine("- **Complexidade**: `" + skill.Complexity + "`");
            sb.AppendLine("- **Agentes Responsáveis**: `" + string.Join(", ", skill.AssignedAgents ?? new List<string>()) + "`");
            sb.AppendLine("- **Modo de Execução**: `" + skill.ExecutionKind + "` (" + skill.ScriptFileName + ")");
            sb.AppendLine("- **Status de Verificação**: " + (skill.Verified ? "✅ Comprovado e Aprovado" : "⚠️ Pendente"));
            sb.AppendLine("- **Data de Criação**: " + skill.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
            sb.AppendLine("- **Versão**: " + Math.Max(1, skill.Version));
            sb.AppendLine();
            if (skill.Lessons != null && skill.Lessons.Count > 0)
            {
                sb.AppendLine("## Lições aprendidas");
                foreach (var lesson in skill.Lessons) sb.AppendLine("- " + lesson);
                sb.AppendLine();
            }
            if (skill.TriggerPatterns != null && skill.TriggerPatterns.Count > 0)
            {
                sb.AppendLine("## Padrões de Ativação");
                foreach (var p in skill.TriggerPatterns) sb.AppendLine("- `" + p + "`");
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(skill.VerificationCommand))
            {
                sb.AppendLine("## Comando de Verificação");
                sb.AppendLine("```powershell");
                sb.AppendLine(skill.VerificationCommand);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(skill.VerificationProof))
            {
                sb.AppendLine("## Evidência da Verificação");
                sb.AppendLine("```");
                sb.AppendLine(skill.VerificationProof.Trim());
                sb.AppendLine("```");
                sb.AppendLine();
            }
            if (skill.IsInstructional)
            {
                sb.AppendLine("## Instruções Operacionais");
                sb.AppendLine(string.IsNullOrWhiteSpace(skill.Instructions) ? skill.Description : skill.Instructions);
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("## Instruções de Uso");
                sb.AppendLine("Execute o arquivo `" + skill.ScriptFileName + "` passando os parâmetros operacionais necessários.");
                sb.AppendLine();
            }
            sb.AppendLine(GeneratedMarker);
            return sb.ToString();
        }

        public static string GenerateActiveSkillsSystemPrompt()
        {
            Initialize();
            var all = GetAll();
            if (all.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine("\n[CATÁLOGO DE SKILLS OPERACIONAIS DA FRIDA]:");
            sb.AppendLine("Habilidades registradas e disponiveis (divulgacao progressiva):");
            foreach (var s in all)
            {
                string status = s.Verified ? "VERIFICADA" : "RASCUNHO";
                string kind = s.IsInstructional ? "instrucional" : (s.ExecutionKind ?? "script");
                sb.AppendLine("- **" + s.Name + "** (`" + s.Id + "`, complexidade: " + s.Complexity + ", status: " + status + (s.IsInstructional ? ", tipo: instrucional" : "") + "): " + s.Description);
            }
            sb.AppendLine("Para carregar instrucoes operacionais detalhadas ou codigo de uma skill, use a ferramenta 'manage_skills' com operation='read' e skill_id='<id>'.");
            return sb.ToString();
        }
    }

    public sealed class FridaSkillEvolutionResult
    {
        public string Summary, SkillId, SkillName, VerificationOutput;
        public bool Passed;
    }

    public static class FridaSkillOrchestrator
    {
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        public static FridaSkillComplexity ClassifyComplexity(string request, string managerDecision)
        {
            // The manager's own rating decides; the request text is only read when the manager gave none.
            string declared = FridaInteractionPolicy.Normalize(managerDecision);
            if (Regex.IsMatch(declared, @"^(extreme|extrema)$")) return FridaSkillComplexity.Extreme;
            if (Regex.IsMatch(declared, @"^(high|alta)$")) return FridaSkillComplexity.High;
            if (Regex.IsMatch(declared, @"^(medium|media)$")) return FridaSkillComplexity.Medium;
            if (Regex.IsMatch(declared, @"^(low|baixa)$")) return FridaSkillComplexity.Low;
            string text = FridaInteractionPolicy.Normalize((request ?? "") + " " + (managerDecision ?? ""));
            if (Regex.IsMatch(text, @"\b(driver|kernel|hooking|descompilacao|patch binario|engenharia reversa profunda)\b"))
                return FridaSkillComplexity.Extreme;
            if (Regex.IsMatch(text, @"\b(solidworks|autocad|automacao com|objeto com|com interop|multiplos sistemas|banco de dados|rede profunda|servico em lote)\b"))
                return FridaSkillComplexity.High;
            if (Regex.IsMatch(text, @"\b(api externa|formato|conversao|ffmpeg|extracao|processamento|json|csv|parser)\b"))
                return FridaSkillComplexity.Medium;
            return FridaSkillComplexity.Low;
        }

        public static List<TeamRole> SelectAgentsForComplexity(FridaSkillComplexity complexity)
        {
            var list = new List<TeamRole>();
            list.Add(TeamRole.Manager); // DeepSeek V4 Pro sempre lidera
            switch (complexity)
            {
                case FridaSkillComplexity.Low:
                    list.Add(TeamRole.Technician); // Groq implementa
                    list.Add(TeamRole.Analyst);    // Cerebras audita
                    break;
                case FridaSkillComplexity.Medium:
                    list.Add(TeamRole.Assistant);  // DeepSeek Chat pesquisa APIs/CLI
                    list.Add(TeamRole.Technician); // Groq codifica
                    list.Add(TeamRole.Analyst);    // Cerebras audita
                    break;
                case FridaSkillComplexity.High:
                case FridaSkillComplexity.Extreme:
                    list.Add(TeamRole.Assistant);  // Pesquisa profunda
                    list.Add(TeamRole.Technician); // Implementacao robusta
                    list.Add(TeamRole.Analyst);    // Auditoria rigorosa e testes
                    list.Add(TeamRole.Secretary);  // Notificacao e consolidacao
                    break;
            }
            return list;
        }

        public static async Task<string> EvolveSkillAsync(
            string request,
            FridaSettings settings,
            Func<TeamRole, string, Task<string>> queryModel,
            Func<string, Task<string>> executeTool,
            Func<string, Task> progress)
        {
            return (await EvolveSkillDetailedAsync(request, settings, queryModel, executeTool, progress).ConfigureAwait(false)).Summary;
        }

        // Same run, with the verification outcome kept apart from the text so callers never report a
        // draft skill as verified.
        public static async Task<FridaSkillEvolutionResult> EvolveSkillDetailedAsync(
            string request,
            FridaSettings settings,
            Func<TeamRole, string, Task<string>> queryModel,
            Func<string, Task<string>> executeTool,
            Func<string, Task> progress)
        {
            FridaWorkContext.ThrowIfCancelled();
            if (progress != null) await progress("Gestor (DeepSeek V4 Pro): Avaliando solicitacao e verificando arquivos e requisitos da skill...").ConfigureAwait(false);

            // 0. Auto-extracao de documentos locais mencionados no pedido (PDFs, planilhas, docs)
            string localDocContext = "";
            var pathMatches = Regex.Matches(request ?? "", @"[a-zA-Z]:\\[^""'\r\n]+?\.(?:pdf|docx?|xlsx?|csv|txt)\b", RegexOptions.IgnoreCase);
            foreach (Match m in pathMatches)
            {
                string filePath = m.Value.Trim();
                if (File.Exists(filePath))
                {
                    try
                    {
                        if (progress != null) await progress("Gestor: Extraindo dados do documento local: " + Path.GetFileName(filePath)).ConfigureAwait(false);
                        string docText = FridaDocumentIO.ReadText(filePath, 0, 16000);
                        localDocContext += "\n\n[CONTEUDO EXTRAIDO DO ARQUIVO LOCAL: " + Path.GetFileName(filePath) + "]\n" + docText;
                    }
                    catch (Exception ex)
                    {
                        BootLogger.Log("Falha ao extrair documento local na evolucao de skill: " + ex.Message);
                    }
                }
            }

            var existingSkills = FridaSkillRegistry.ListSkills();
            string skillsSummary = existingSkills.Count > 0
                ? "Skills ativas no catalogo: " + string.Join(", ", existingSkills.Select(s => s.Id + " (" + s.Name + ")"))
                : "Nenhuma skill previa registrada.";

            // 1. Gestor (DeepSeek V4 Pro) - Analise de Complexidade e Especificacao
            string managerPrompt =
                "Você é o Gestor / Cérebro da Frida (DeepSeek V4 Pro). O operador solicitou uma nova capacidade ou atualizacao de skill:\n"
                + "\"" + request + "\"\n\n"
                + skillsSummary + "\n"
                + localDocContext + "\n\n"
                + "DIRETRIZES DO GESTOR:\n"
                + "1. Se a solicitacao for enriquecer/atualizar uma skill ja existente no catalogo (ex: incorporar regras de POP, novas etapas), use exatamente o 'skill_id' existente.\n"
                + "2. Se for uma capacidade inedita, defina um novo 'skill_id' slug em minusculas.\n"
                + "3. O comando de teste (verification_command) executa dentro da pasta da skill (ex: 'python run.py --check'), precisa rodar o script da skill com uma entrada de teste e retornar 0 somente se o resultado estiver correto. Um comando que apenas imprime texto (Write-Output, echo) e recusado e a skill fica como rascunho.\n"
                + "Responda OBRIGATORIAMENTE em JSON válido com:\n"
                + "{\n"
                + "  \"skill_id\": \"slug-da-skill\",\n"
                + "  \"skill_name\": \"Nome Claro da Habilidade\",\n"
                + "  \"description\": \"O que a skill faz e quando acioná-la\",\n"
                + "  \"complexity\": \"Low | Medium | High | Extreme\",\n"
                + "  \"research_directives\": \"O que o Estagiário deve pesquisar (apenas se faltar metodo tecnico na maquina; PROIBIDO vazar nomes internos confidenciais na web)\",\n"
                + "  \"implementation_criteria\": \"O que o Técnico deve implementar ou atualizar no script (regras de negocio, entradas, saídas)\",\n"
                + "  \"verification_command\": \"python run.py --check\",\n"
                + "  \"trigger_keywords\": [\"palavra1\", \"palavra2\"]\n"
                + "}";

            string managerRaw = await queryModel(TeamRole.Manager, managerPrompt).ConfigureAwait(false);
            var managerSpec = ExtractJson(managerRaw);

            string skillId = GetStr(managerSpec, "skill_id", "nova-skill-" + DateTime.UtcNow.Ticks);
            string skillName = GetStr(managerSpec, "skill_name", "Nova Skill Frida");
            string description = GetStr(managerSpec, "description", request);
            string complexityStr = GetStr(managerSpec, "complexity", "Medium");
            string researchDirectives = GetStr(managerSpec, "research_directives", request);
            string implementationCriteria = GetStr(managerSpec, "implementation_criteria", "");
            string verificationCommand = GetStr(managerSpec, "verification_command", "");
            var triggerKeywords = GetList(managerSpec, "trigger_keywords");

            FridaSkillComplexity complexity = ClassifyComplexity(request, complexityStr);
            var selectedAgents = SelectAgentsForComplexity(complexity);

            if (progress != null)
                await progress("Complexidade definida: " + complexity + ". Agentes ativados: " + string.Join(", ", selectedAgents) + ".").ConfigureAwait(false);

            // 2. Estagiario (DeepSeek Chat) - Descoberta de Formas de Fazer
            string researchFindings = "Execução local padrão com ferramentas existentes.";
            if (selectedAgents.Contains(TeamRole.Assistant))
            {
                if (progress != null) await progress("Estagiário (DeepSeek Chat): Descobrindo formas de fazer, sintaxes e APIs recomendadas...").ConfigureAwait(false);
                string assistantPrompt =
                    "Você é o Estagiário / Pesquisador da equipe Frida (DeepSeek Chat).\n"
                    + "O Gestor determinou a pesquisa para a skill '" + skillName + "' (" + skillId + "):\n"
                    + "Diretrizes: " + researchDirectives + "\n\n"
                    + "Descubra a forma técnica mais robusta, rápida e limpa de implementar esta funcionalidade no Windows 11 nativo "
                    + "(usando PowerShell 5.1/Core, Python 3 ou ferramentas CLI já disponíveis).\n"
                    + "Apresente: (1) Comandos exatos, (2) Parâmetros recomendados, (3) Código de exemplo enxuto, (4) Como testar e verificar.";

                researchFindings = await queryModel(TeamRole.Assistant, assistantPrompt).ConfigureAwait(false);
            }

            // 3. Tecnico (Groq Brain) - Sintese e Implementacao
            if (progress != null) await progress("Técnico (Groq Brain): Codificando script e definindo estrutura da skill...").ConfigureAwait(false);
            string techPrompt =
                "Você é o Técnico / Engenheiro de Software da equipe Frida (Groq Brain).\n"
                + "Implemente o script oficial da skill '" + skillName + "' (" + skillId + ").\n"
                + "Critérios do Gestor: " + implementationCriteria + "\n"
                + "Descoberta do Estagiário:\n" + researchFindings + "\n\n"
                + "Entregue o script completo em PowerShell (ou Python se exigido) pronto para salvar e executar.\n"
                + "Responda em JSON com:\n"
                + "{\n"
                + "  \"script_filename\": \"run.ps1\",\n"
                + "  \"script_content\": \"<codigo completo do script aqui>\",\n"
                + "  \"verification_command\": \"" + verificationCommand + "\"\n"
                + "}";

            string techRaw = await queryModel(TeamRole.Technician, techPrompt).ConfigureAwait(false);
            var techSpec = ExtractJson(techRaw);

            string scriptFilename = GetStr(techSpec, "script_filename", "run.ps1");
            string scriptContent = GetStr(techSpec, "script_content", "# Skill " + skillName + "\nparam($target)\nWrite-Output \"Executando " + skillName + " em $target\"");
            if (!string.IsNullOrWhiteSpace(GetStr(techSpec, "verification_command", "")))
                verificationCommand = GetStr(techSpec, "verification_command", verificationCommand);

            // 4. Analista (Cerebras Brain) - Auditoria e Verificacao Pratica
            if (progress != null) await progress("Analista (Cerebras Brain): Auditando código e executando teste de verificação...").ConfigureAwait(false);

            // Salva provisoriamente para teste (preserva historico se ja existir)
            var existingEntry = FridaSkillRegistry.Get(skillId);
            var skillEntry = existingEntry ?? new FridaSkillEntry();
            skillEntry.Id = skillId;
            skillEntry.Name = !string.IsNullOrWhiteSpace(skillName) ? skillName : skillEntry.Name;
            skillEntry.Description = !string.IsNullOrWhiteSpace(description) ? description : skillEntry.Description;
            skillEntry.Complexity = complexity;
            skillEntry.AssignedAgents = selectedAgents.Select(a => a.ToString()).ToList();
            if (triggerKeywords != null && triggerKeywords.Count > 0)
            {
                foreach (var tk in triggerKeywords)
                    if (!skillEntry.TriggerKeywords.Contains(tk, StringComparer.OrdinalIgnoreCase))
                        skillEntry.TriggerKeywords.Add(tk);
            }
            if (!string.IsNullOrWhiteSpace(scriptFilename))
                skillEntry.ScriptFileName = scriptFilename;
            if (!string.IsNullOrWhiteSpace(scriptContent))
                skillEntry.ScriptContent = scriptContent;
            skillEntry.ExecutionKind = (skillEntry.ScriptFileName ?? "").EndsWith(".py", StringComparison.OrdinalIgnoreCase) ? "python" : "powershell";
            if (!string.IsNullOrWhiteSpace(verificationCommand))
                skillEntry.VerificationCommand = verificationCommand;
            skillEntry.Verified = false;
            FridaSkillRegistry.RegisterAndSave(skillEntry);

            // Executa verificacao real no sistema
            var verifyResult = FridaSkillRegistry.VerifySkill(skillId);
            bool passed = verifyResult.Status == FridaResultStatus.Verified || verifyResult.Status == FridaResultStatus.ProcessCompleted;

            // Se o teste falhou, aciona 1 ciclo de auto-correcao
            if (!passed)
            {
                if (progress != null) await progress("Analista reprovou teste inicial (" + verifyResult.Output + "). Técnico aplicando correção...").ConfigureAwait(false);
                string fixPrompt =
                    "O teste de verificação da skill '" + skillId + "' falhou com o erro:\n"
                    + verifyResult.Output + "\n"
                    + "Comando testado: " + verificationCommand + "\n"
                    + "Corrija o script ou o comando de teste para funcionar corretamente no Windows. Responda em JSON com script_content e verification_command.";

                string fixRaw = await queryModel(TeamRole.Technician, fixPrompt).ConfigureAwait(false);
                var fixSpec = ExtractJson(fixRaw);
                skillEntry.ScriptContent = GetStr(fixSpec, "script_content", skillEntry.ScriptContent);
                skillEntry.VerificationCommand = GetStr(fixSpec, "verification_command", skillEntry.VerificationCommand);
                FridaSkillRegistry.RegisterAndSave(skillEntry);

                verifyResult = FridaSkillRegistry.VerifySkill(skillId);
                passed = verifyResult.Status == FridaResultStatus.Verified || verifyResult.Status == FridaResultStatus.ProcessCompleted;
            }

            // 5. Consolidacao e Execucao da solicitacao original
            string summary =
                (passed ? "### ✅ Nova Skill Aprendida e Verificada: " : "### ⚠️ Skill registrada como rascunho (verificação não aprovada): ") + skillName + " (`" + skillId + "`)\n"
                + "- **Complexidade**: " + complexity + "\n"
                + "- **Agentes**: " + string.Join(" → ", selectedAgents.Select(a => a == TeamRole.Manager ? "Gestor (DeepSeek V4 Pro)" : a == TeamRole.Assistant ? "Estagiário" : a == TeamRole.Technician ? "Técnico" : a == TeamRole.Analyst ? "Analista" : "Secretária")) + "\n"
                + "- **Status de Verificação**: " + (passed ? "Comprovado com sucesso via teste real" : "Reprovada ou sem teste válido — a skill não está pronta para reuso") + "\n"
                + "- **Diretório**: `" + skillEntry.SkillDirectory + "`\n"
                + "- **Evidência do Teste**: `" + Truncate(verifyResult.Output, 250) + "`\n\n";

            if (progress != null) await progress(passed
                ? "Skill " + skillName + " aprendida e verificada. Executando a solicitação do operador..."
                : "Skill " + skillName + " ficou como rascunho: a verificação não foi aprovada. Executando a solicitação do operador sem depender dela...").ConfigureAwait(false);

            // Executa a tarefa solicitada; uma skill em rascunho nao e apresentada como capacidade pronta.
            if (executeTool != null)
            {
                string runResult = await executeTool((passed
                    ? "Solicitação atendida com a nova skill '" + skillName + "':\n"
                    : "A skill '" + skillName + "' ficou como rascunho e não foi verificada; atenda ao pedido com as ferramentas normais e não a apresente como pronta:\n") + request).ConfigureAwait(false);
                summary += "### Resultado Operacional:\n" + runResult;
            }

            return new FridaSkillEvolutionResult { Summary = summary, SkillId = skillEntry.Id, SkillName = skillName, Passed = passed, VerificationOutput = verifyResult.Output };
        }

        public static async Task<string> EvaluateAndRegisterLearnedSkillAsync(
            string request,
            string executionResult,
            string sources,
            FridaSettings settings,
            Func<TeamRole, string, Task<string>> queryModel,
            Func<string, Task> progress)
        {
            if (string.IsNullOrWhiteSpace(request) || queryModel == null) return null;
            FridaWorkContext.ThrowIfCancelled();

            string learnPrompt =
                "Você é o Gestor / Cérebro da Frida (DeepSeek V4 Pro).\n"
                + "Acabamos de atender com sucesso à seguinte solicitação do operador:\n\"" + Truncate(request, 500) + "\"\n\n"
                + "Relato e evidências da execução técnica:\n" + Truncate(executionResult, 1000) + "\n\n"
                + "Fontes/pesquisas consultadas nesta demanda:\n" + Truncate(sources, 500) + "\n\n"
                + "DIRETRIZ DE APRENDIZADO CONTÍNUO DE SKILLS:\n"
                + "Avalie se esta demanda exigiu pesquisar como fazer, instalar algo novo, criar um script ou descobrir um procedimento técnico que deva ser registrado como uma SKILL PERMANENTE no catálogo da Frida, para que ela saiba repetir isso imediatamente no futuro.\n"
                + "Se SIM, responda OBRIGATORIAMENTE em JSON válido com:\n"
                + "{\n"
                + "  \"learn_new_skill\": true,\n"
                + "  \"skill_id\": \"slug-da-nova-habilidade\",\n"
                + "  \"skill_name\": \"Nome Claro da Habilidade\",\n"
                + "  \"description\": \"O que a skill faz e como resolve tarefas similares\",\n"
                + "  \"complexity\": \"Low | Medium | High | Extreme\",\n"
                + "  \"trigger_keywords\": [\"termo1\", \"termo2\"],\n"
                + "  \"execution_kind\": \"powershell | python\",\n"
                + "  \"script_filename\": \"run.ps1 | run.py\",\n"
                + "  \"script_content\": \"<codigo consolidado do procedimento/script>\",\n"
                + "  \"verification_command\": \"<comando que roda o script com uma entrada de teste e retorna 0 somente se funcionar, ex.: python run.py --check>\"\n"
                + "}\n"
                + "So registre quando houver um script real e reutilizavel. Um comando de verificacao que apenas imprime texto e recusado e a skill fica como rascunho.\n"
                + "Se NÃO houve novo aprendizado (foi apenas conversa comum ou uso de comando trivial já conhecido sem nada novo), responda exatamente:\n"
                + "{\"learn_new_skill\": false}";

            try
            {
                string raw = await queryModel(TeamRole.Manager, learnPrompt).ConfigureAwait(false);
                var spec = ExtractJson(raw);
                if (spec != null && spec.ContainsKey("learn_new_skill") && Convert.ToBoolean(spec["learn_new_skill"]))
                {
                    string skillId = GetStr(spec, "skill_id", "skill-" + DateTime.UtcNow.Ticks);
                    string skillName = GetStr(spec, "skill_name", "Nova Capacidade");
                    string description = GetStr(spec, "description", request);
                    string compStr = GetStr(spec, "complexity", "Medium");
                    var triggers = GetList(spec, "trigger_keywords");
                    string kind = GetStr(spec, "execution_kind", "powershell");
                    string scriptFile = GetStr(spec, "script_filename", kind == "python" ? "run.py" : "run.ps1");
                    string scriptContent = GetStr(spec, "script_content", "");
                    string verCmd = GetStr(spec, "verification_command", "");
                    // Without a script there is no procedure to reuse; nothing is added to the catalog.
                    if (!scriptContent.Split('\n').Any(line => { string l = line.Trim(); return l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("//") && !l.StartsWith("<"); }))
                    {
                        BootLogger.Log("Aprendizado de skill descartado: o Gestor nao entregou um script reutilizavel para '" + skillId + "'.");
                        return null;
                    }

                    FridaSkillComplexity comp = ClassifyComplexity(request, compStr);
                    var entry = new FridaSkillEntry
                    {
                        Id = skillId,
                        Name = skillName,
                        Description = description,
                        Complexity = comp,
                        AssignedAgents = new List<string> { "Manager", "Technician", "Analyst" },
                        TriggerKeywords = triggers,
                        ExecutionKind = kind,
                        ScriptFileName = scriptFile,
                        ScriptContent = scriptContent,
                        VerificationCommand = verCmd,
                        Verified = false
                    };
                    FridaSkillRegistry.RegisterAndSave(entry);
                    var verify = FridaSkillRegistry.VerifySkill(skillId);
                    bool passed = verify.Status == FridaResultStatus.Verified || verify.Status == FridaResultStatus.ProcessCompleted;

                    string learnedMsg = "Gestor (Cérebro): " + (passed ? "Nova skill aprendida e verificada" : "Skill registrada como rascunho, sem verificação aprovada") + " -> " + skillName + " (`" + skillId + "`).";
                    BootLogger.Log(learnedMsg);
                    if (progress != null) await progress(learnedMsg).ConfigureAwait(false);

                    return "\n\n[NOVA SKILL INCORPORADA PELO GESTOR]\n"
                        + "- **Habilidade**: " + skillName + " (`" + skillId + "`)\n"
                        + "- **Objetivo**: " + description + "\n"
                        + "- **Gatilhos**: " + string.Join(", ", triggers) + "\n"
                        + "- **Status**: " + (passed ? "Verificada com sucesso e pronta para reuso." : "Rascunho — a verificação não foi aprovada (" + Truncate(verify.Summary, 160) + "); ainda não está pronta para reuso.");
                }
            }
            catch (Exception ex)
            {
                BootLogger.Log("Falha ao registrar skill aprendida: " + ex.Message);
            }
            return null;
        }

        internal static Dictionary<string, object> ExtractJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object>();
            try
            {
                Match m = Regex.Match(text, @"\{[\s\S]*\}");
                if (m.Success)
                    return _json.Deserialize<Dictionary<string, object>>(m.Value) ?? new Dictionary<string, object>();
            }
            catch { }
            return new Dictionary<string, object>();
        }

        internal static string GetStr(Dictionary<string, object> dict, string key, string fallback)
        {
            object val;
            if (dict != null && dict.TryGetValue(key, out val) && val != null)
                return Convert.ToString(val).Trim();
            return fallback;
        }

        internal static List<string> GetList(Dictionary<string, object> dict, string key)
        {
            var res = new List<string>();
            object val;
            if (dict != null && dict.TryGetValue(key, out val) && val is ArrayList)
            {
                foreach (var item in (ArrayList)val)
                    if (item != null) res.Add(Convert.ToString(item).Trim());
            }
            return res;
        }

        internal static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
