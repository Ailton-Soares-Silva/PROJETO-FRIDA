using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public sealed class FridaRemoteExecution
    {
        public string Id;
        public FridaIdeProject Project;
        public string Command;
        public string Status;
        public string ExitCode;
        public string FilePath;
        public string ExpectedHash;
        public override string ToString() { return Project.SshAlias + " · " + Status + " · " + Id; }
    }

    public static class FridaSshWorkspace
    {
        internal sealed class TransportResult
        {
            public int ExitCode;
            public string Output;
            public string Error;
            public bool Interrupted;
        }
        internal static Func<FridaIdeProject, string, string, int, bool, CancellationToken, TransportResult> TransportOverride = null;
        public static bool IsRemote { get { return FridaIdeRuntime.Context != null && FridaIdeRuntime.Context.Project != null && !string.IsNullOrWhiteSpace(FridaIdeRuntime.Context.Project.SshAlias); } }
        public static string ShellQuote(string value)
        {
            if (value == null || value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) throw new ArgumentException("Caminho remoto inválido.");
            return "'" + value.Replace("'", "'\"'\"'") + "'";
        }
        private static string WindowsQuote(string value)
        {
            return "\"" + Regex.Replace(Regex.Replace(value, @"(\\*)""", "$1$1\\\""), @"(\\+)$", "$1$1") + "\"";
        }
        public static void ValidateProject(FridaIdeProject project)
        {
            if (project == null || !Regex.IsMatch(project.SshAlias ?? "", @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,252}$")) throw new ArgumentException("Selecione um alias SSH válido.");
            if (string.IsNullOrWhiteSpace(project.Root) || !project.Root.StartsWith("/")) throw new ArgumentException("Informe a raiz absoluta no servidor, por exemplo /home/usuario/projeto.");
            ShellQuote(project.Root);
        }
        public static string ResolvePath(string path)
        {
            var project = FridaIdeRuntime.Context.Project;
            ValidateProject(project);
            if (string.IsNullOrWhiteSpace(path)) return project.Root;
            ShellQuote(path);
            if (path.Contains("\\")) throw new IOException("Use diretórios Linux no projeto remoto.");
            return path.StartsWith("/") ? path : project.Root.TrimEnd('/') + "/" + path;
        }
        public static string[] Aliases()
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
            if (!File.Exists(file)) return new string[0];
            return File.ReadLines(file).Where(line => Regex.IsMatch(line, @"^\s*Host\s+", RegexOptions.IgnoreCase))
                .SelectMany(line => Regex.Split(line.Trim(), @"\s+").Skip(1)).Where(alias => Regex.IsMatch(alias, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,252}$")).Distinct().ToArray();
        }
        private static TransportResult Transport(FridaIdeProject project, string executable, string payload, int timeoutMs, bool stream, CancellationToken cancellation)
        {
            ValidateProject(project);
            if (TransportOverride != null) return TransportOverride(project, executable, payload, timeoutMs, stream, cancellation);
            string arguments = "-o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=10 ";
            arguments += executable == "ssh.exe" ? "-T " + WindowsQuote(project.SshAlias) + " sh -s" : "-b - " + WindowsQuote(project.SshAlias);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "OpenSSH", executable), arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new IOException("O cliente SSH não iniciou.");
                var output = new StringBuilder(); var errors = new StringBuilder();
                Task stdout = Task.Run(() => Drain(process.StandardOutput, output, stream, "Saída"));
                Task stderr = Task.Run(() => Drain(process.StandardError, errors, stream, "Erro"));
                Task input = Task.Run(() =>
                {
                    using (var bytes = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
                    { bytes.Write(payload.Replace("\r\n", "\n")); bytes.Flush(); }
                });
                var clock = Stopwatch.StartNew();
                while (!process.WaitForExit(100))
                {
                    if (cancellation.IsCancellationRequested || clock.ElapsedMilliseconds > timeoutMs)
                    {
                        FridaVerifiedTools.KillOwnedProcessTree(process);
                        Task.WaitAll(new[] { stdout, stderr }, 3000);
                        return new TransportResult { ExitCode = -1, Output = output.ToString(), Error = errors.ToString(), Interrupted = true };
                    }
                }
                if (!Task.WaitAll(new[] { stdout, stderr, input }, 5000)) throw new IOException("A conexão encerrou sem confirmar todos os dados.");
                return new TransportResult { ExitCode = process.ExitCode, Output = output.ToString(), Error = errors.ToString() };
            }
        }
        private static void Drain(StreamReader input, StringBuilder stored, bool stream, string label)
        {
            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (stored.Length < 262144) stored.AppendLine(line.Substring(0, Math.Min(line.Length, 262144 - stored.Length)));
                if (stream) FridaIdeRuntime.Publish(label + " · " + line);
            }
        }
        private static string Query(FridaIdeProject project, string script)
        {
            var result = Transport(project, "ssh.exe", script + "\n", 30000, false, FridaWorkContext.Token);
            if (result.Interrupted || result.ExitCode != 0) throw new IOException("SSH não confirmou a operação. " + FridaIdeRuntime.Redact(result.Error));
            return result.Output;
        }
        public static string Probe(FridaIdeProject project)
        {
            return Query(project, "set -eu\ntest -d " + ShellQuote(project.Root) + "\ncd " + ShellQuote(project.Root) + "\nprintf 'Conectado em '\nhostname\npwd\ncommand -v sh\ncommand -v sha256sum\ncommand -v setsid");
        }
        public static void CreateProject(FridaIdeProject project)
        {
            Query(project, "set -eu\ntest ! -e " + ShellQuote(project.Root) + "\nmkdir -p -- " + ShellQuote(project.Root) + "\ntest -d " + ShellQuote(project.Root));
        }
        public static void OpenIdentityCheck(string alias)
        {
            if (!Regex.IsMatch(alias ?? "", @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,252}$")) throw new ArgumentException("Alias SSH inválido.");
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe"), "-o StrictHostKeyChecking=ask -o ConnectTimeout=10 " + WindowsQuote(alias) + " exit") { UseShellExecute = true });
        }
        private static string SftpPath(string path)
        {
            ShellQuote(path);
            return "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
        private static void Transfer(FridaIdeProject project, string batch)
        {
            var result = Transport(project, "sftp.exe", batch + "\n", 60000, false, FridaWorkContext.Token);
            if (result.Interrupted || result.ExitCode != 0) throw new IOException("Transferência SFTP não confirmada. " + FridaIdeRuntime.Redact(result.Error));
        }
        public static string Read(FridaIdeProject project, string path)
        {
            string directory = Path.Combine(FridaIdeRuntime.DataRoot, "Transfers"); Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Query(project, "test -f " + ShellQuote(path) + " && test $(wc -c < " + ShellQuote(path) + ") -le 2097152");
                Transfer(project, "get " + SftpPath(path) + " " + SftpPath(temporary.Replace('\\', '/')));
                return File.ReadAllText(temporary, Encoding.UTF8);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static string Hash(FridaIdeProject project, string path)
        {
            string output = Query(project, "if test -f " + ShellQuote(path) + "; then sha256sum -- " + ShellQuote(path) + "; else printf 'MISSING'; fi");
            if (output.Trim() == "MISSING") return null;
            var match = Regex.Match(output, @"^[a-fA-F0-9]{64}");
            if (!match.Success) throw new IOException("O servidor não retornou o hash do arquivo.");
            return match.Value.ToLowerInvariant();
        }
        public static void Write(FridaIdeProject project, string path, string text, string expectedHash)
        {
            if (FridaVerifiedTools.BinaryDocument(path) || path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) throw new IOException("Formato binário exige uma ferramenta apropriada de geração.");
            FridaVerifiedTools.ValidateTextFormat(path, text);

            string before = Hash(project, path);
            if (before != expectedHash) throw new IOException("O arquivo remoto mudou. Compare as versões antes de salvar.");
            string directory = Path.Combine(FridaIdeRuntime.DataRoot, "Transfers"); Directory.CreateDirectory(directory);
            string local = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp"), remote = path + ".frida-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(local, text, new UTF8Encoding(false));
            string wanted = FridaVerifiedTools.FileSha256(local);
            var write = new FridaRemoteExecution { Id = Guid.NewGuid().ToString("N"), Project = project, Command = "Gravar arquivo", FilePath = path, ExpectedHash = wanted, Status = "gravação a confirmar" };
            SaveExecution(write);
            try
            {
                Query(project, "mkdir -p -- " + ShellQuote(path.Substring(0, path.LastIndexOf('/'))));
                Transfer(project, "put " + SftpPath(local.Replace('\\', '/')) + " " + SftpPath(remote));
                if (Hash(project, remote) != wanted || Hash(project, path) != before) throw new IOException("Os arquivos mudaram durante a transferência. A gravação foi suspensa.");
                string precondition = before == null
                    ? "test ! -e " + ShellQuote(path)
                    : "actual=$(sha256sum -- " + ShellQuote(path) + "); test \"${actual%% *}\" = " + ShellQuote(before);
                Query(project, "set -eu\n" + precondition + "\nmv -f -- " + ShellQuote(remote) + " " + ShellQuote(path));
                if (Hash(project, path) != wanted) throw new IOException("O conteúdo remoto ainda precisa ser confirmado.");
                write.Status = "arquivo conferido"; SaveExecution(write);
                FridaIdeRuntime.Publish("Arquivo remoto salvo e conferido · " + path + " · " + write.Id);
            }
            catch (IOException)
            {
                FridaIdeRuntime.Publish("Gravação remota com resultado a confirmar · " + path + " · SHA-256 esperado " + wanted);
                throw;
            }
            finally { File.Delete(local); }
        }
        public static bool IsDirectory(FridaIdeProject project, string path)
        {
            return Query(project, "if test -d " + ShellQuote(path) + "; then printf DIRECTORY; else printf FILE; fi").Trim() == "DIRECTORY";
        }
        public static string[] List(FridaIdeProject project, string path)
        {
            return Query(project, "find " + ShellQuote(path) + " -mindepth 1 -maxdepth 1 -printf '%y %p\\n' | head -n 500").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
        private static void SaveExecution(FridaRemoteExecution execution)
        {
            string directory = Path.Combine(FridaIdeRuntime.DataRoot, "RemoteExecutions"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, execution.Id + ".json"), temporary = path + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(execution), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        public static FridaToolReceipt Run(FridaIdeProject project, string command, int timeoutMs)
        {
            ValidateProject(project);
            var receipt = new FridaToolReceipt("run_powershell", "execute_remote", project.SshAlias, true);
            string id = receipt.OperationId, folder = "/tmp/frida-" + id;
            var execution = new FridaRemoteExecution { Id = id, Project = project, Command = FridaIdeRuntime.Redact(command), Status = "resultado a confirmar" };
            SaveExecution(execution);
            FridaIdeRuntime.Publish("SSH · execução " + id + " · " + project.Root + "\r\n" + command);
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
            string script = "set -u\ncd " + ShellQuote(project.Root) + " || exit 97\ncommand -v setsid >/dev/null || exit 96\n" +
                "umask 077\nmkdir " + ShellQuote(folder) + " || exit 95\nprintf '%s' '" + encoded + "' | base64 -d > " + ShellQuote(folder + "/task.sh") + "\n" +
                "setsid sh " + ShellQuote(folder + "/task.sh") + " &\npid=$!\nstart=$(awk '{print $22}' /proc/$pid/stat)\nprintf '%s %s\n' \"$pid\" \"$start\" > " + ShellQuote(folder + "/pid") + "\n" +
                "wait \"$pid\"\ncode=$?\nprintf '%s' \"$code\" > " + ShellQuote(folder + "/exit") + "\nexit \"$code\"\n";
            try
            {
                var result = Transport(project, "ssh.exe", script, timeoutMs, true, FridaWorkContext.Token);
                receipt.Put("execution_id", id); receipt.Put("stdout", result.Output); receipt.Put("stderr", result.Error);
                if (result.Interrupted)
                {
                    string cancellation = "set -eu\nif test -f " + ShellQuote(folder + "/exit") + "; then cat " + ShellQuote(folder + "/exit") + "; exit; fi\n" +
                        "read pid start < " + ShellQuote(folder + "/pid") + "\ncurrent=$(awk '{print $22}' /proc/$pid/stat)\ntest \"$current\" = \"$start\"\nkill -TERM -\"$pid\"\nsleep 1\nif kill -0 -\"$pid\" 2>/dev/null; then exit 90; fi\nprintf 'CANCELLED'\n";
                    var stopped = Transport(project, "ssh.exe", cancellation, 15000, false, CancellationToken.None);
                    int completedCode;
                    if (stopped.ExitCode == 0 && !stopped.Interrupted && int.TryParse(stopped.Output.Trim(), out completedCode))
                    {
                        execution.Status = "encerrada antes da interrupção"; execution.ExitCode = completedCode.ToString(); SaveExecution(execution);
                        receipt.Put("exit_code", completedCode);
                        return receipt.Finish(completedCode == 0 ? FridaResultStatus.ProcessCompleted : FridaResultStatus.Failed, "remote_process_exit", "Processo remoto já encerrado, código " + completedCode + ".", result.Output + result.Error);
                    }
                    execution.Status = stopped.ExitCode == 0 && !stopped.Interrupted && stopped.Output.Trim() == "CANCELLED" ? "interrupção confirmada" : "resultado a confirmar";
                    SaveExecution(execution);
                    return receipt.Finish(FridaResultStatus.Unconfirmed, "remote_interrupted", execution.Status + ". Efeitos anteriores exigem conferência.", result.Output + result.Error);
                }
                string remoteCode = Query(project, "cat " + ShellQuote(folder + "/exit")).Trim();
                if (remoteCode != result.ExitCode.ToString()) throw new IOException("O término remoto ainda não foi confirmado.");
                execution.Status = "encerrada"; execution.ExitCode = remoteCode; SaveExecution(execution);
                receipt.Put("exit_code", result.ExitCode);
                receipt.Check("remote_exit", result.ExitCode == 0, "0", remoteCode, "ssh_and_remote_exit_file");
                return receipt.Finish(result.ExitCode == 0 ? FridaResultStatus.ProcessCompleted : FridaResultStatus.Failed, "remote_process_exit", "Execução SSH terminou com código " + remoteCode, result.Output + "\n" + result.Error);
            }
            catch (Exception ex)
            {
                return receipt.Finish(FridaResultStatus.Unconfirmed, "remote_unknown", "Resultado remoto a confirmar. " + ex.Message);
            }
        }
        public static bool SupportsTool(string name)
        {
            return new[] { "manage_files", "inspect_project", "read_code", "search_symbols", "apply_patch", "verify_artifact" }.Contains(name);
        }
        private static string Arg(Dictionary<string, object> args, string key)
        {
            object value; return args.TryGetValue(key, out value) ? Convert.ToString(value) : "";
        }
        public static FridaToolReceipt ExecuteTool(string name, Dictionary<string, object> args)
        {
            var project = FridaIdeRuntime.Context.Project;
            string path = ResolvePath(Arg(args, name == "search_symbols" ? "root_path" : "path"));
            string operation = name == "manage_files" ? Arg(args, "operation") : name;
            var receipt = new FridaToolReceipt(name, operation, path, operation == "write" || operation == "append" || operation == "apply_patch" || operation == "mkdir" || operation == "delete");
            try
            {
                if (name == "run_project_validation") return Run(project, Arg(args, "command"), 120000);
                if (name == "inspect_project" || operation == "list" || operation == "search")
                    return receipt.Finish(FridaResultStatus.Partial, "remote_directory", "Itens remotos, limitados a 500.", string.Join("\n", List(project, path)));
                if (name == "search_symbols")
                {
                    string result = Query(project, "grep -rnF -- " + ShellQuote(Arg(args, "query")) + " " + ShellQuote(path) + " | head -n 100");
                    return receipt.Finish(FridaResultStatus.Partial, "remote_search", "Busca limitada a 100 correspondências.", result);
                }
                if (operation == "mkdir")
                {
                    Query(project, "mkdir -p -- " + ShellQuote(path) + " && test -d " + ShellQuote(path));
                    receipt.Check("remote_directory_exists", true, "exists", "exists", "ssh_test_directory");
                    return receipt.Finish(FridaResultStatus.Verified, "remote_directory_exists", "Pasta remota criada e conferida.");
                }
                if (operation == "delete")
                {
                    MoveToRecovery(project, path);
                    return receipt.Finish(FridaResultStatus.Submitted, "remote_recovery", "Item movido à pasta de recuperação do projeto.");
                }
                if (operation == "write" || operation == "append" || name == "apply_patch")
                {
                    string before = Hash(project, path), content = Arg(args, "content");
                    if (operation == "append") content = (before == null ? "" : Read(project, path)) + content;
                    if (name == "apply_patch")
                    {
                        if (before != Arg(args, "expected_before_sha256")) throw new IOException("O hash do patch diverge do arquivo remoto.");
                        string existing = Read(project, path), target = Arg(args, "target_text");
                        if (string.IsNullOrEmpty(target) || existing.IndexOf(target, StringComparison.Ordinal) < 0 || existing.IndexOf(target, existing.IndexOf(target, StringComparison.Ordinal) + target.Length, StringComparison.Ordinal) >= 0) throw new IOException("O trecho deve ocorrer exatamente uma vez.");
                        content = existing.Replace(target, Arg(args, "replacement_text"));
                    }
                    Write(project, path, content, before);
                    receipt.Put("before_sha256", before); receipt.Put("sha256", Hash(project, path));
                    receipt.Check("remote_file_sha256", true, "uploaded_sha256", "readback_equal", "sftp_and_sha256sum");
                    return receipt.Finish(FridaResultStatus.Verified, "remote_file_content", "Arquivo remoto gravado e conferido por SHA-256.");
                }
                if (name == "verify_artifact")
                {
                    string actual = Hash(project, path), expected = Arg(args, "expected_sha256");
                    if (actual == null) throw new IOException("Arquivo remoto ausente.");
                    if (Arg(args, "kind") != "sha256" && Arg(args, "kind") != "exists") throw new IOException("Para verificar sintaxe ou testes remotos, execute o comando de validação do projeto.");
                    bool valid = expected.Length == 0 || actual == expected;
                    receipt.Check("remote_sha256", valid, expected.Length == 0 ? "file_exists" : expected, actual, "ssh_sha256sum");
                    return receipt.Finish(valid ? FridaResultStatus.Verified : FridaResultStatus.Failed, "remote_file_hash", actual);
                }
                string text = Read(project, path);
                if (name == "read_code")
                {
                    int first, last;
                    if (!int.TryParse(Arg(args, "start_line"), out first)) first = 1;
                    if (!int.TryParse(Arg(args, "end_line"), out last)) last = first + 199;
                    first = Math.Max(1, first); last = Math.Min(first + 999, Math.Max(first, last));
                    text = string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select((line, index) => (index + 1) + ": " + line).Skip(first - 1).Take(last - first + 1));
                }
                return receipt.Finish(FridaResultStatus.Partial, "remote_text_snapshot", "Texto remoto obtido por SFTP, limitado a 2 MB.", text);
            }
            catch (Exception ex)
            {
                if (receipt.IsAction) return receipt.Finish(FridaResultStatus.Unconfirmed, "remote_operation_unknown", "Operação remota a confirmar. " + ex.Message);
                return receipt.Fail("remote_operation_failed", ex.Message);
            }
        }
        public static void Move(FridaIdeProject project, string source, string destination)
        {
            Query(project, "set -eu\ntest ! -e " + ShellQuote(destination) + "\nmv -T -n -- " + ShellQuote(source) + " " + ShellQuote(destination) + "\ntest ! -e " + ShellQuote(source) + "\ntest -e " + ShellQuote(destination));
            FridaIdeRuntime.Publish("Item remoto movido · " + source + " → " + destination);
        }
        public static void MoveToRecovery(FridaIdeProject project, string path)
        {
            string root = project.Root.TrimEnd('/');
            if (!path.StartsWith(root + "/", StringComparison.Ordinal) || path.Split('/').Contains("..")) throw new IOException("Selecione um item dentro do projeto para recuperar depois.");
            string directory = root + "/.frida-lixeira";
            Query(project, "mkdir -p -- " + ShellQuote(directory));
            string destination = directory + "/" + Guid.NewGuid().ToString("N") + "-" + path.Substring(path.LastIndexOf('/') + 1);
            Move(project, path, destination);
            FridaIdeRuntime.Publish("Recuperação disponível · " + destination + " → " + path);
        }
        public static List<FridaRemoteExecution> Executions()
        {
            var result = new List<FridaRemoteExecution>();
            string directory = Path.Combine(FridaIdeRuntime.DataRoot, "RemoteExecutions");
            if (!Directory.Exists(directory)) return result;
            foreach (string file in Directory.GetFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(200))
            {
                try { result.Add(new JavaScriptSerializer().Deserialize<FridaRemoteExecution>(File.ReadAllText(file, Encoding.UTF8))); }
                catch (Exception ex) { BootLogger.Log("Registro remoto indisponível: " + ex.GetType().Name); }
            }
            return result;
        }
        public static string Reconcile(string id)
        {
            if (!Regex.IsMatch(id, @"^[a-f0-9]{32}$")) throw new ArgumentException("Execução inválida.");
            string path = Path.Combine(FridaIdeRuntime.DataRoot, "RemoteExecutions", id + ".json");
            var execution = new JavaScriptSerializer().Deserialize<FridaRemoteExecution>(File.ReadAllText(path));
            if (!string.IsNullOrEmpty(execution.FilePath))
            {
                string actual = Hash(execution.Project, execution.FilePath);
                if (actual != execution.ExpectedHash) throw new IOException("Conteúdo remoto diferente do esperado. Compare o arquivo antes de tentar novamente.");
                execution.Status = "arquivo conferido"; SaveExecution(execution);
                return execution.Project.SshAlias + " · Arquivo confirmado por SHA-256 · " + execution.FilePath;
            }
            string code = Query(execution.Project, "cat " + ShellQuote("/tmp/frida-" + id + "/exit")).Trim();
            int number;
            if (!int.TryParse(code, out number)) throw new IOException("O servidor ainda não confirmou o término.");
            execution.ExitCode = code; execution.Status = "encerrada"; SaveExecution(execution);
            return execution.Project.SshAlias + " · " + id + " · Código de saída " + code;
        }
    }
}
