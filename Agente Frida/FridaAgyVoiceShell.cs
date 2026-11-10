// language: C# 5 (.NET Framework 4.8), file: FridaAgyVoiceShell.cs, target: Windows 10/11 x64
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FridaAgent
{
    internal sealed class AntigravityBridgeResponse
    {
        public string Status;
        public string Text;
        public string Spoken;
        public bool HasRichOutput;
        public string ErrorMessage;
    }

    internal sealed class AntigravityBridgeClient : IDisposable
    {
        private readonly string _baseDir;
        private readonly JavaScriptSerializer _json;
        private readonly object _procLock = new object();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<AntigravityBridgeResponse>> _pending =
            new ConcurrentDictionary<string, TaskCompletionSource<AntigravityBridgeResponse>>(StringComparer.Ordinal);

        private Process _proc;
        private StreamWriter _stdin;
        private volatile bool _disposed;
        private volatile bool _isReady;

        public event Action<string> OnToolExecuting;
        public event Action<string> OnBridgeStatus;

        public bool IsReady { get { return _isReady; } }

        public AntigravityBridgeClient(string baseDir)
        {
            _baseDir = baseDir;
            _json = new JavaScriptSerializer();
            _json.MaxJsonLength = 64 * 1024 * 1024;
        }

        private string ResolvePythonExe()
        {
            string venvPy = Path.Combine(_baseDir, @".venv-agy\Scripts\python.exe");
            if (File.Exists(venvPy)) return venvPy;
            return "python.exe";
        }

        public void Start()
        {
            if (_disposed) return;
            lock (_procLock)
            {
                if (_proc != null && !_proc.HasExited) return;
                string scriptPath = Path.Combine(_baseDir, "frida_agy_bridge.py");
                if (!File.Exists(scriptPath))
                    throw new FileNotFoundException("frida_agy_bridge.py nao encontrado.", scriptPath);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = ResolvePythonExe();
                psi.Arguments = "-u \"" + scriptPath + "\"";
                psi.WorkingDirectory = _baseDir;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                _isReady = false;
                _proc = Process.Start(psi);
                _stdin = new StreamWriter(_proc.StandardInput.BaseStream, new UTF8Encoding(false));
                _stdin.AutoFlush = true;

                Process captured = _proc;
                Thread outThread = new Thread(delegate() { ReadStdoutLoop(captured); });
                outThread.IsBackground = true;
                outThread.Name = "FridaAgyBridgeOut";
                outThread.Start();

                Thread errThread = new Thread(delegate() { ReadStderrLoop(captured); });
                errThread.IsBackground = true;
                errThread.Name = "FridaAgyBridgeErr";
                errThread.Start();
            }
        }

        private void ReadStdoutLoop(Process proc)
        {
            try
            {
                while (!_disposed && proc != null && !proc.HasExited)
                {
                    string line = proc.StandardOutput.ReadLine();
                    if (line == null) break;
                    line = line.Trim();
                    if (line.Length == 0 || line[0] != '{') continue;

                    Dictionary<string, object> msg = null;
                    try { msg = _json.Deserialize<Dictionary<string, object>>(line); }
                    catch { continue; }
                    if (msg == null || !msg.ContainsKey("event")) continue;

                    string ev = Convert.ToString(msg["event"] ?? "");
                    string reqId = msg.ContainsKey("request_id") ? Convert.ToString(msg["request_id"] ?? "") : "";

                    if (ev == "ready")
                    {
                        _isReady = true;
                        if (OnBridgeStatus != null) OnBridgeStatus("Antigravity conectado");
                    }
                    else if (ev == "tool")
                    {
                        string toolName = msg.ContainsKey("tool") ? Convert.ToString(msg["tool"] ?? "ferramenta") : "ferramenta";
                        if (OnToolExecuting != null) OnToolExecuting(toolName);
                    }
                    else if (ev == "reply")
                    {
                        TaskCompletionSource<AntigravityBridgeResponse> tcs;
                        if (_pending.TryRemove(reqId, out tcs))
                        {
                            AntigravityBridgeResponse r = new AntigravityBridgeResponse();
                            r.Status = "ok";
                            r.Text = msg.ContainsKey("text") ? Convert.ToString(msg["text"] ?? "") : "";
                            r.Spoken = msg.ContainsKey("spoken") ? Convert.ToString(msg["spoken"] ?? "") : r.Text;
                            r.HasRichOutput = msg.ContainsKey("has_rich_output") && Convert.ToBoolean(msg["has_rich_output"]);
                            tcs.TrySetResult(r);
                        }
                    }
                    else if (ev == "cancelled")
                    {
                        TaskCompletionSource<AntigravityBridgeResponse> tcs;
                        if (_pending.TryRemove(reqId, out tcs))
                        {
                            tcs.TrySetCanceled();
                        }
                    }
                    else if (ev == "error")
                    {
                        string errText = msg.ContainsKey("message") ? Convert.ToString(msg["message"] ?? "Erro desconhecido") : "Erro desconhecido";
                        BootLogger.Log("AgyBridge erro: " + errText);
                        TaskCompletionSource<AntigravityBridgeResponse> tcs;
                        if (!string.IsNullOrEmpty(reqId) && _pending.TryRemove(reqId, out tcs))
                        {
                            AntigravityBridgeResponse r = new AntigravityBridgeResponse();
                            r.Status = "error";
                            r.ErrorMessage = errText;
                            tcs.TrySetResult(r);
                        }
                        else if (OnBridgeStatus != null)
                        {
                            OnBridgeStatus("Erro: " + errText);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                BootLogger.Log("AgyBridge stdout encerrado: " + ex.Message);
            }
            finally
            {
                _isReady = false;
                foreach (KeyValuePair<string, TaskCompletionSource<AntigravityBridgeResponse>> kv in _pending)
                {
                    TaskCompletionSource<AntigravityBridgeResponse> removed;
                    if (_pending.TryRemove(kv.Key, out removed))
                    {
                        AntigravityBridgeResponse r = new AntigravityBridgeResponse();
                        r.Status = "error";
                        r.ErrorMessage = "Processo da ponte Antigravity reiniciado.";
                        removed.TrySetResult(r);
                    }
                }
            }
        }

        private void ReadStderrLoop(Process proc)
        {
            try
            {
                while (!_disposed && proc != null && !proc.HasExited)
                {
                    string line = proc.StandardError.ReadLine();
                    if (line == null) break;
                    if (!string.IsNullOrWhiteSpace(line))
                        BootLogger.Log("AgyBridge[stderr]: " + line);
                }
            }
            catch { }
        }

        private void SendJson(Dictionary<string, object> payload)
        {
            Start();
            lock (_procLock)
            {
                if (_stdin == null) throw new InvalidOperationException("Pipe Antigravity indisponivel.");
                string raw = _json.Serialize(payload);
                _stdin.WriteLine(raw);
            }
        }

        public async Task<AntigravityBridgeResponse> SendPromptAsync(string prompt, string channel, CancellationToken token)
        {
            Start();
            for (int wait = 0; wait < 120 && !_isReady && !_disposed; wait++)
            {
                await Task.Delay(100, token);
            }
            string reqId = Guid.NewGuid().ToString("N");
            TaskCompletionSource<AntigravityBridgeResponse> tcs = new TaskCompletionSource<AntigravityBridgeResponse>();
            _pending[reqId] = tcs;

            using (token.Register(delegate()
            {
                CancelActiveTurn();
                TaskCompletionSource<AntigravityBridgeResponse> ignored;
                if (_pending.TryRemove(reqId, out ignored)) ignored.TrySetCanceled();
            }))
            {
                Dictionary<string, object> req = new Dictionary<string, object>();
                req["op"] = "chat";
                req["request_id"] = reqId;
                req["channel"] = channel ?? "voice";
                req["text"] = prompt;
                SendJson(req);
                return await tcs.Task;
            }
        }

        public void CancelActiveTurn()
        {
            try
            {
                lock (_procLock)
                {
                    if (_proc == null || _proc.HasExited || _stdin == null) return;
                    _stdin.WriteLine("{\"op\":\"cancel\"}");
                }
            }
            catch { }
        }

        public void ResetSession()
        {
            try
            {
                Dictionary<string, object> req = new Dictionary<string, object>();
                req["op"] = "reset";
                SendJson(req);
            }
            catch { }
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_procLock)
            {
                try { if (_stdin != null) _stdin.Close(); } catch { }
                try
                {
                    if (_proc != null && !_proc.HasExited)
                        FridaVerifiedTools.KillOwnedProcessTree(_proc);
                }
                catch { }
                _proc = null;
                _stdin = null;
            }
        }
    }

    internal sealed class FridaAgyConsoleForm : Form
    {
        private readonly RichTextBox _transcriptBox;
        private readonly TextBox _inputBox;
        private readonly Label _statusLabel;
        private readonly Button _sendBtn;
        private readonly Button _micBtn;
        private readonly Button _stopBtn;
        private readonly Action<string> _onSendText;
        private readonly Action _onMicTrigger;
        private readonly Action _onStopTrigger;

        public FridaAgyConsoleForm(Action<string> onSendText, Action onMicTrigger, Action onStopTrigger)
        {
            _onSendText = onSendText;
            _onMicTrigger = onMicTrigger;
            _onStopTrigger = onStopTrigger;

            Text = "Frida · Motor Antigravity (Voz + Bancada)";
            ClientSize = new Size(860, 560);
            MinimumSize = new Size(640, 420);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(12, 19, 33);
            ForeColor = Color.FromArgb(228, 238, 250);
            Font = new Font("Segoe UI", 10f);

            Panel topBar = new Panel();
            topBar.Dock = DockStyle.Top;
            topBar.Height = 36;
            topBar.BackColor = Color.FromArgb(16, 28, 48);
            topBar.Padding = new Padding(12, 8, 12, 6);

            _statusLabel = new Label();
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.ForeColor = Color.FromArgb(110, 210, 255);
            _statusLabel.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            _statusLabel.Text = "Motor Antigravity pronto · Diga \"Frida\", aperte F4 ou digite abaixo.";
            topBar.Controls.Add(_statusLabel);

            Panel bottomBar = new Panel();
            bottomBar.Dock = DockStyle.Bottom;
            bottomBar.Height = 52;
            bottomBar.Padding = new Padding(10, 8, 10, 10);
            bottomBar.BackColor = Color.FromArgb(16, 26, 44);

            _inputBox = new TextBox();
            _inputBox.Dock = DockStyle.Fill;
            _inputBox.BackColor = Color.FromArgb(22, 35, 58);
            _inputBox.ForeColor = Color.White;
            _inputBox.BorderStyle = BorderStyle.FixedSingle;
            _inputBox.Font = new Font("Segoe UI", 10.5f);
            _inputBox.KeyDown += delegate(object s, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Enter && !ke.Shift)
                {
                    ke.SuppressKeyPress = true;
                    SubmitInput();
                }
            };

            _sendBtn = MakeActionButton("Enviar", Color.FromArgb(0, 132, 228), 88);
            _sendBtn.Dock = DockStyle.Right;
            _sendBtn.Click += delegate { SubmitInput(); };

            _micBtn = MakeActionButton("Voz (F4)", Color.FromArgb(24, 96, 152), 92);
            _micBtn.Dock = DockStyle.Right;
            _micBtn.Click += delegate { if (_onMicTrigger != null) _onMicTrigger(); };

            _stopBtn = MakeActionButton("Parar", Color.FromArgb(135, 42, 52), 78);
            _stopBtn.Dock = DockStyle.Right;
            _stopBtn.Click += delegate { if (_onStopTrigger != null) _onStopTrigger(); };

            Panel btnSpacer1 = new Panel { Dock = DockStyle.Right, Width = 6 };
            Panel btnSpacer2 = new Panel { Dock = DockStyle.Right, Width = 6 };
            Panel btnSpacer3 = new Panel { Dock = DockStyle.Right, Width = 8 };

            bottomBar.Controls.Add(_inputBox);
            bottomBar.Controls.Add(btnSpacer3);
            bottomBar.Controls.Add(_micBtn);
            bottomBar.Controls.Add(btnSpacer2);
            bottomBar.Controls.Add(_stopBtn);
            bottomBar.Controls.Add(btnSpacer1);
            bottomBar.Controls.Add(_sendBtn);

            _transcriptBox = new RichTextBox();
            _transcriptBox.Dock = DockStyle.Fill;
            _transcriptBox.ReadOnly = true;
            _transcriptBox.BorderStyle = BorderStyle.None;
            _transcriptBox.BackColor = Color.FromArgb(12, 19, 33);
            _transcriptBox.ForeColor = Color.FromArgb(228, 238, 250);
            _transcriptBox.Font = new Font("Consolas", 10f);
            _transcriptBox.DetectUrls = true;

            Controls.Add(_transcriptBox);
            Controls.Add(bottomBar);
            Controls.Add(topBar);

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
        }

        private static Button MakeActionButton(string text, Color bg, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = width;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = bg;
            b.ForeColor = Color.White;
            b.Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold);
            return b;
        }

        private void SubmitInput()
        {
            string val = (_inputBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(val)) return;
            _inputBox.Clear();
            if (_onSendText != null) _onSendText(val);
        }

        public void SetStatus(string status)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatus), status); return; }
            _statusLabel.Text = status ?? "";
        }

        public void AppendTurn(string speaker, string body, Color headerColor)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<string, string, Color>(AppendTurn), speaker, body, headerColor); return; }
            // Shared cached fonts (two new Font objects per turn were never disposed) and the reply
            // without Markdown/JSON symbols, as in the chat.
            _transcriptBox.SelectionStart = _transcriptBox.TextLength;
            _transcriptBox.SelectionColor = headerColor;
            _transcriptBox.SelectionFont = FridaTheme.Strong(10f);
            _transcriptBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + speaker + "\r\n");

            _transcriptBox.SelectionStart = _transcriptBox.TextLength;
            _transcriptBox.SelectionColor = FridaTheme.Ink;
            _transcriptBox.SelectionFont = FridaTheme.Font(10f);
            _transcriptBox.AppendText(FridaMarkdown.ToPlain(FridaMarkdown.Readable((body ?? "").Trim())) + "\r\n\r\n");
            _transcriptBox.ScrollToCaret();
        }

        public void FocusInput()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            _inputBox.Focus();
        }
    }

    public sealed class FridaAgyTrayController : Form
    {
        public const string WakeEventName = "Local\\AgenteFridaAntigravityWake_V1";
        private const int HotkeyActivateF4 = 9101;
        private const int HotkeyConsole = 9102;
        private const int MOD_ALT = 0x0001;

        private readonly string _baseDir;
        private readonly string _configPath;
        private readonly FridaSettings _settings;
        private readonly HttpClient _http;
        private readonly HologramOrbForm _orb;
        private readonly AudioVoiceEngine _voice;
        private readonly AntigravityBridgeClient _bridge;
        private readonly NotifyIcon _trayIcon;
        private readonly FridaAgyConsoleForm _console;

        private ToolStripMenuItem _miPauseWake;
        private ToolStripMenuItem _miStartup;
        private VoiceEnrollmentForm _enrollmentForm;
        private EventWaitHandle _wakeEvent;
        private RegisteredWaitHandle _wakeReg;
        private CancellationTokenSource _turnCts;
        private int _turnEpoch;
        private volatile bool _processingTurn;

        public FridaAgyTrayController()
        {
            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _configPath = Path.Combine(_baseDir, "config.json");
            _settings = FridaSettings.LoadOrCreate(_configPath);

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(75);

            _orb = new HologramOrbForm(_settings, _configPath);
            _orb.Show();
            _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");

            _voice = new AudioVoiceEngine(_settings, _orb, _http);
            _voice.OnUserTranscriptReady += delegate(string txt, bool wake) { HandleTranscript(txt, wake, "voice"); };
            _voice.OnPriorityControl += HandlePriorityControl;
            _voice.OnVoiceTrainingRequired += delegate { BeginInvoke(new Action(OpenVoiceEnrollment)); };
            _voice.OnNaturalVoiceUnavailable += delegate(string txt)
            {
                BeginInvoke(new Action(delegate()
                {
                    _console.AppendTurn("FRIDA (Texto)", txt, Color.FromArgb(255, 215, 110));
                    _console.FocusInput();
                }));
            };

            _bridge = new AntigravityBridgeClient(_baseDir);
            _bridge.OnToolExecuting += delegate(string toolName)
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(delegate()
                    {
                        _orb.SetActivityState(OrbActivityState.Executing, "FRIDA · ANTIGRAVITY", "Executando: " + toolName);
                        _console.SetStatus("Executando ferramenta Antigravity: " + toolName);
                    }));
                }
            };
            _bridge.OnBridgeStatus += delegate(string st)
            {
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(new Action(delegate() { _console.SetStatus(st); }));
            };

            _console = new FridaAgyConsoleForm(
                delegate(string typed) { HandleTranscript(typed, false, "text"); },
                delegate() { ActivateOrInterrupt(); },
                delegate() { HandlePriorityControl(FridaControlCommand.Stop); }
            );
            Icon appIcon = LoadAppIcon();
            _console.Icon = appIcon;

            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = appIcon;
            _trayIcon.Text = "Frida · Motor Antigravity Ativo";
            _trayIcon.Visible = true;
            _trayIcon.ContextMenuStrip = BuildTrayMenu();
            _trayIcon.MouseClick += delegate(object s, MouseEventArgs me)
            {
                if (me.Button == MouseButtons.Left) ActivateOrInterrupt();
            };
            _trayIcon.MouseDoubleClick += delegate(object s, MouseEventArgs me)
            {
                if (me.Button == MouseButtons.Left) _console.FocusInput();
            };

            SyncWindowsStartup(_settings.start_with_windows);
            if (!IsHandleCreated) CreateHandle();

            System.Windows.Forms.Timer bootTimer = new System.Windows.Forms.Timer();
            bootTimer.Interval = 120;
            bootTimer.Tick += delegate
            {
                bootTimer.Stop();
                bootTimer.Dispose();
                InitializeRuntimeAfterHandle();
            };
            bootTimer.Start();
        }

        private void InitializeRuntimeAfterHandle()
        {
            try { SystemWin32.RegisterHotKey(Handle, HotkeyActivateF4, SystemWin32.MOD_NONE, SystemWin32.VK_F4); } catch { }
            try { SystemWin32.RegisterHotKey(Handle, HotkeyConsole, SystemWin32.MOD_CONTROL | MOD_ALT, (int)Keys.F); } catch { }

            try
            {
                _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
                _wakeReg = ThreadPool.RegisterWaitForSingleObject(_wakeEvent, delegate(object st, bool timedOut)
                {
                    if (!timedOut && !IsDisposed && IsHandleCreated)
                        BeginInvoke(new Action(ActivateOrInterrupt));
                }, null, -1, false);
            }
            catch { }

            Task.Run(delegate()
            {
                try
                {
                    _bridge.Start();
                    BootLogger.Log("Ponte Antigravity (frida_agy_bridge.py) iniciada.");
                }
                catch (Exception ex) { BootLogger.Log("Falha ao subir frida_agy_bridge: " + ex.Message); }

                try
                {
                    // Auto-bypass virtual dummy mic (e.g., Steam Streaming Mic at index 0) in memory
                    List<MicDeviceInfo> allMics = _voice.ListTrainingMicrophones();
                    if (_settings.preferred_mic_device >= 0 && _settings.preferred_mic_device < allMics.Count)
                    {
                        string prefName = (allMics[_settings.preferred_mic_device].Name ?? "").ToLowerInvariant();
                        if (prefName.Contains("steam streaming") || prefName.Contains("virtual"))
                        {
                            BootLogger.Log("Ignorando microfone virtual salvo (" + allMics[_settings.preferred_mic_device].Name + ") -> auto-selecionando microfone fisico.");
                            _settings.preferred_mic_device = -1;
                        }
                    }

                    _voice.Start();
                    BootLogger.Log("AudioVoiceEngine (Antigravity) ativo. Mic: [" + _voice.ActiveMicDeviceId + "] " + _voice.ActiveMicName);
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(delegate()
                        {
                            string tip = "Frida (Antigravity) — Mic: " + (_voice.ActiveMicName ?? "Padrao");
                            _trayIcon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
                        }));
                    }
                    _voice.CancelFollowUpAndHide();
                }
                catch (Exception ex)
                {
                    BootLogger.Log("Falha ao iniciar audio: " + ex.Message);
                }
            });
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem miListen = new ToolStripMenuItem("Falar com Frida agora (F4)");
            miListen.Font = new Font(menu.Font, FontStyle.Bold);
            miListen.Click += delegate { ActivateOrInterrupt(); };
            menu.Items.Add(miListen);

            ToolStripMenuItem miConsole = new ToolStripMenuItem("Abrir Console Antigravity (Ctrl+Alt+F)");
            miConsole.Click += delegate { _console.FocusInput(); };
            menu.Items.Add(miConsole);

            ToolStripMenuItem miReset = new ToolStripMenuItem("Nova Sessao Antigravity (Limpar Contexto)");
            miReset.Click += delegate
            {
                _bridge.ResetSession();
                _console.AppendTurn("SISTEMA", "Sessao Antigravity reiniciada.", Color.LightSkyBlue);
            };
            menu.Items.Add(miReset);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miVoiceEnroll = new ToolStripMenuItem("Treinar / Verificar Minha Voz (Biometria)");
            miVoiceEnroll.Click += delegate { OpenVoiceEnrollment(); };
            menu.Items.Add(miVoiceEnroll);

            ToolStripMenuItem miVoiceOptions = new ToolStripMenuItem("Opções de Voz da Frida");
            ToolStripMenuItem miFrancisca = new ToolStripMenuItem("Francisca (Edge Neural · Grátis)");
            ToolStripMenuItem miAoede = new ToolStripMenuItem("Aoede (Google Gemini · Natural)");
            ToolStripMenuItem miDemonstrateVoice = new ToolStripMenuItem("Ouvir Demonstração da Voz Atual...");

            miFrancisca.Click += delegate
            {
                _settings.prefer_zero_quota_tts = true;
                FridaSettings.Save(_settings, _configPath);
                miFrancisca.Checked = true;
                miAoede.Checked = false;
                if (!_voice.IsOff)
                {
                    Task.Run(async () => {
                        try { await _voice.SpeakResponseAsync("Voz alterada para Francisca.", false); } catch { }
                    });
                }
            };
            miAoede.Click += delegate
            {
                _settings.prefer_zero_quota_tts = false;
                _settings.female_voice = "Aoede";
                FridaSettings.Save(_settings, _configPath);
                miFrancisca.Checked = false;
                miAoede.Checked = true;
                if (!_voice.IsOff)
                {
                    Task.Run(async () => {
                        try { await _voice.SpeakResponseAsync("Voz alterada para Aoede.", false); } catch { }
                    });
                }
            };
            miDemonstrateVoice.Click += async delegate
            {
                string v = _settings.prefer_zero_quota_tts ? "Francisca" : "Aoede";
                await _voice.SpeakResponseAsync("Olá! Esta é a voz " + v + " da Frida.", false);
            };

            miVoiceOptions.DropDownItems.Add(miFrancisca);
            miVoiceOptions.DropDownItems.Add(miAoede);
            miVoiceOptions.DropDownItems.Add(new ToolStripSeparator());
            miVoiceOptions.DropDownItems.Add(miDemonstrateVoice);

            menu.Items.Add(miVoiceOptions);

            ToolStripMenuItem miMics = new ToolStripMenuItem("Selecionar Microfone");
            menu.Opening += delegate
            {
                miFrancisca.Checked = _settings.prefer_zero_quota_tts;
                miAoede.Checked = !_settings.prefer_zero_quota_tts;
                miMics.DropDownItems.Clear();
                List<MicDeviceInfo> devices = _voice.ListTrainingMicrophones();
                for (int i = 0; i < devices.Count; i++)
                {
                    MicDeviceInfo dev = devices[i];
                    ToolStripMenuItem item = new ToolStripMenuItem("[" + dev.DeviceId + "] " + dev.Name);
                    item.Checked = (dev.DeviceId == _voice.ActiveMicDeviceId);
                    item.Click += delegate
                    {
                        _voice.SelectTrainingMicrophone(dev.DeviceId, dev.Name);
                        string tip = "Frida (Antigravity) — Mic: " + dev.Name;
                        _trayIcon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
                    };
                    miMics.DropDownItems.Add(item);
                }
            };
            menu.Items.Add(miMics);

            _miPauseWake = new ToolStripMenuItem("Pausar Escuta por Voz");
            _miPauseWake.CheckOnClick = true;
            _miPauseWake.Click += delegate
            {
                _voice.IsPaused = _miPauseWake.Checked;
                if (_miPauseWake.Checked) _voice.EnterOff();
            };
            menu.Items.Add(_miPauseWake);

            _miStartup = new ToolStripMenuItem("Iniciar junto com o Windows");
            _miStartup.Checked = _settings.start_with_windows;
            _miStartup.CheckOnClick = true;
            _miStartup.Click += delegate
            {
                _settings.start_with_windows = _miStartup.Checked;
                FridaSettings.Save(_settings, _configPath);
                SyncWindowsStartup(_settings.start_with_windows);
            };
            menu.Items.Add(_miStartup);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miExit = new ToolStripMenuItem("Sair");
            miExit.Click += delegate { CleanExit(); };
            menu.Items.Add(miExit);

            return menu;
        }

        private void SyncWindowsStartup(bool enable)
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (rk == null) return;
                    string exePath = "\"" + Application.ExecutablePath + "\"";
                    if (enable) rk.SetValue("AgenteFridaAntigravity", exePath);
                    else rk.DeleteValue("AgenteFridaAntigravity", false);
                }
            }
            catch { }
        }

        private void OpenVoiceEnrollment()
        {
            if (_enrollmentForm != null && !_enrollmentForm.IsDisposed)
            {
                _enrollmentForm.Activate();
                return;
            }
            _enrollmentForm = new VoiceEnrollmentForm(_voice);
            _enrollmentForm.Icon = LoadAppIcon();
            _enrollmentForm.Show();
        }

        private void ActivateOrInterrupt()
        {
            if (_processingTurn || _voice.IsSpeaking)
            {
                HandlePriorityControl(FridaControlCommand.Stop);
                return;
            }
            if (_miPauseWake != null && _miPauseWake.Checked)
            {
                _miPauseWake.Checked = false;
                _voice.IsPaused = false;
            }
            _voice.TriggerManualOrWakeListen(false);
        }

        private void HandlePriorityControl(FridaControlCommand cmd)
        {
            Interlocked.Increment(ref _turnEpoch);
            CancellationTokenSource cts = _turnCts;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
            _bridge.CancelActiveTurn();
            _voice.StopCurrentSpeech();
            _processingTurn = false;
            _voice.IsBusyProcessingTurn = false;

            if (cmd == FridaControlCommand.Off)
            {
                _voice.EnterOff();
                _console.SetStatus("Frida em modo OFF (aperte F4 para reativar).");
            }
            else
            {
                _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
                _console.SetStatus("Execucao interrompida.");
            }
        }

        private static bool IsClosingPhrase(string text)
        {
            string norm = FridaInteractionPolicy.Normalize(text);
            return Regex.IsMatch(norm, @"^(?:obrigad[ao]|valeu|era so isso|so isso|dispensad[ao]|tchau|ate mais|encerrar atendimento)$");
        }

        private void HandleTranscript(string userText, bool wasWakeWord, string channel)
        {
            FridaControlCommand ctrl = FridaSessionControl.Parse(userText);
            if (ctrl != FridaControlCommand.None)
            {
                HandlePriorityControl(ctrl);
                return;
            }

            if (userText == "__WAKE_ONLY__")
            {
                if (!_voice.IsOff && !_processingTurn)
                    _voice.WaitForExplicitRequest();
                return;
            }

            if (string.IsNullOrWhiteSpace(userText) || _voice.IsEnrollingVoice) return;
            if (_processingTurn)
            {
                HandlePriorityControl(FridaControlCommand.Stop);
            }

            _processingTurn = true;
            int myEpoch = Interlocked.Increment(ref _turnEpoch);
            CancellationTokenSource cts = new CancellationTokenSource();
            _turnCts = cts;

            _console.AppendTurn("VOCE (" + channel.ToUpperInvariant() + ")", userText, Color.FromArgb(115, 215, 255));

            FridaWorkContext.RunIndependent(async delegate()
            {
                _voice.IsBusyProcessingTurn = true;
                try
                {
                    if (IsClosingPhrase(userText))
                    {
                        bool bye = Regex.IsMatch(FridaInteractionPolicy.Normalize(userText), @"\b(tchau|dispensad[ao]|ate mais|encerrar)\b");
                        await _voice.SpeakResponseAsync(bye ? "Atendimento encerrado." : "Por nada.", false);
                        if (myEpoch != Volatile.Read(ref _turnEpoch)) return;
                        _voice.IsBusyProcessingTurn = false;
                        if (bye) _voice.EnterOff();
                        else _voice.WaitForConversationContinuation();
                        return;
                    }

                    BeginInvoke(new Action(delegate()
                    {
                        _orb.SetActivityState(OrbActivityState.Thinking, "FRIDA · ANTIGRAVITY", "Processando no Antigravity...");
                        _console.SetStatus("Antigravity processando...");
                    }));

                    AntigravityBridgeResponse res = await _bridge.SendPromptAsync(userText, channel, cts.Token);
                    if (myEpoch != Volatile.Read(ref _turnEpoch)) return;

                    if (res.Status == "error")
                    {
                        string msg = "Falha na ponte Antigravity: " + res.ErrorMessage;
                        _console.AppendTurn("ERRO", msg, Color.Salmon);
                        _console.SetStatus(msg);
                        await _voice.SpeakTextAsync("Houve uma falha na ponte Antigravity. Verifique o console.", null);
                        _voice.EnterOff();
                        return;
                    }

                    _console.AppendTurn("FRIDA · ANTIGRAVITY", res.Text, Color.FromArgb(135, 245, 180));
                    _console.SetStatus("Pronto.");

                    if (res.HasRichOutput || channel == "text")
                    {
                        BeginInvoke(new Action(delegate()
                        {
                            if (!_console.Visible) _console.Show();
                        }));
                    }

                    string toSpeak = !string.IsNullOrWhiteSpace(res.Spoken) ? res.Spoken : res.Text;
                    FridaSpeechResult speech = await _voice.SpeakResponseWithResultAsync(toSpeak, res.HasRichOutput);
                    if (myEpoch != Volatile.Read(ref _turnEpoch)) return;

                    _voice.IsBusyProcessingTurn = false;
                    if (speech != null && speech.Status == "failed") _voice.EnterOff();
                    else if (channel == "voice") _voice.WaitForConversationContinuation();
                    else _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
                }
                catch (OperationCanceledException)
                {
                    if (myEpoch == Volatile.Read(ref _turnEpoch))
                        _orb.SetActivityState(OrbActivityState.Hidden, "FRIDA", "");
                }
                catch (Exception ex)
                {
                    BootLogger.Log("FridaAgy turn EXC: " + ex.Message);
                    _console.AppendTurn("EXCECAO", ex.Message, Color.Salmon);
                }
                finally
                {
                    if (myEpoch == Volatile.Read(ref _turnEpoch))
                    {
                        _voice.IsBusyProcessingTurn = false;
                        _processingTurn = false;
                    }
                }
            });
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == SystemWin32.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HotkeyActivateF4) { ActivateOrInterrupt(); return; }
                if (id == HotkeyConsole) { _console.FocusInput(); return; }
            }
            base.WndProc(ref m);
        }

        private Icon LoadAppIcon()
        {
            string ico = Path.Combine(_baseDir, "frida.ico");
            if (File.Exists(ico))
            {
                try { return new Icon(ico); } catch { }
            }
            using (Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(235, 8, 22, 48)))
                using (Pen ring = new Pen(Color.FromArgb(250, 0, 200, 255), 2.2f))
                using (SolidBrush core = new SolidBrush(Color.FromArgb(255, 195, 250, 255)))
                {
                    g.FillEllipse(bg, 1, 1, 30, 30);
                    g.DrawEllipse(ring, 4, 4, 24, 24);
                    g.FillEllipse(core, 11, 11, 10, 10);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private void CleanExit()
        {
            FridaShutdown.IsShuttingDown = true;
            HandlePriorityControl(FridaControlCommand.Off);
            try { if (_wakeReg != null) _wakeReg.Unregister(null); } catch { }
            try { if (_wakeEvent != null) _wakeEvent.Close(); } catch { }
            try { SystemWin32.UnregisterHotKey(Handle, HotkeyActivateF4); } catch { }
            try { SystemWin32.UnregisterHotKey(Handle, HotkeyConsole); } catch { }
            try { _trayIcon.Visible = false; _trayIcon.Dispose(); } catch { }
            try { _bridge.Dispose(); } catch { }
            try { _voice.Dispose(); } catch { }
            try { _console.Dispose(); } catch { }
            try { _orb.Close(); _orb.Dispose(); } catch { }
            Application.Exit();
        }
    }

    internal static class FridaAgyProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            try { if (!SystemWin32.SetProcessDpiAwarenessContext(new IntPtr(-4))) SystemWin32.SetProcessDPIAware(); }
            catch { try { SystemWin32.SetProcessDPIAware(); } catch { } }

            using (Mutex mutex = new Mutex(false, "Local\\AgenteFridaAntigravityCore_V1"))
            {
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { owned = true; }

                    if (!owned)
                    {
                        try
                        {
                            using (EventWaitHandle wake = EventWaitHandle.OpenExisting(FridaAgyTrayController.WakeEventName))
                                wake.Set();
                        }
                        catch { }
                        return;
                    }

                    FridaRuntimeHealth.Start(AppDomain.CurrentDomain.BaseDirectory);
                    BootLogger.Log("Iniciando FridaAgyAgent (Casca de Voz + Orbe 3D sobre Google Antigravity SDK).");
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new FridaAgyTrayController());
                    FridaRuntimeHealth.Shutdown();
                }
                finally
                {
                    if (owned) mutex.ReleaseMutex();
                }
            }
        }
    }
}
