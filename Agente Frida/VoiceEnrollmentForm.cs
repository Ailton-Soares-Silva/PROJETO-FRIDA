using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FridaAgent
{
    // Cadastro no estilo Voice Match: 5 frases curtas (5 s cada), verificacao neural local.
    public sealed class VoiceEnrollmentForm : Form
    {
        private static readonly string[] Phrases =
        {
            "Oi Frida, que horas são agora?",
            "Frida, abra minha agenda e me diga os compromissos de hoje.",
            "Frida, pesquise o preço do dólar e me explique a variação.",
            "Ei Frida, me lembre de ligar para o contador às três da tarde.",
            "Frida, pare. Depois a gente continua, obrigado."
        };

        private readonly AudioVoiceEngine _voice;
        private readonly Label _microphone = new Label(), _status = new Label(), _levelText = new Label(), _steps = new Label();
        private readonly ProgressBar _progress = new ProgressBar(), _level = new ProgressBar();
        private readonly Button _record = new Button(), _test = new Button(), _testOther = new Button(), _cancel = new Button(), _clear = new Button();
        private readonly TextBox _reading = new TextBox();
        private readonly Timer _refresh = new Timer(), _next = new Timer();
        private readonly double[][] _phrases = new double[SpeakerVoiceProfile.RequiredPhrases][];
        private int _current;
        private VoiceEnrollmentSession _session;
        private bool _reported, _enrolling;

        public VoiceEnrollmentForm(AudioVoiceEngine voice)
        {
            _voice = voice; Text = "Frida — Cadastrar minha voz"; ClientSize = new Size(780, 600);
            MinimumSize = new Size(720, 580); StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10f); BackColor = Color.FromArgb(14, 22, 37); ForeColor = Color.FromArgb(225, 235, 245);
            MaximizeBox = false; TopMost = true;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 9 };
            foreach (int h in new[] { 52, 44, 28, 0, 28, 30, 72, 84, 40 }) layout.RowStyles.Add(h == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, h));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Leia 5 frases curtas no seu tom e distancia normais (5 s cada). Tudo e analisado neste computador. Ruido de fundo e ignorado automaticamente; evite apenas outra pessoa falando durante o cadastro." }, 0, 0);

            var micPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
            micPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); micPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115)); micPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            _microphone.Dock = DockStyle.Fill; _microphone.ForeColor = Color.LightSkyBlue; micPanel.Controls.Add(_microphone, 0, 0);
            var chooseMic = MakeButton("Microfone...", 105); chooseMic.Click += (s, e) => ChooseMicrophone(chooseMic); micPanel.Controls.Add(chooseMic, 1, 0);
            var restart = MakeButton("Reiniciar audio", 130);
            restart.Click += (s, e) => { _voice.RestartTrainingMicrophone(); _session = null; SetStatus("Captacao reiniciada. Aguarde o nivel do microfone aparecer.", Color.LightSkyBlue); RefreshState(); };
            micPanel.Controls.Add(restart, 2, 0); layout.Controls.Add(micPanel, 0, 1);

            _steps.Dock = DockStyle.Fill; _steps.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold); layout.Controls.Add(_steps, 0, 2);
            _reading.Dock = DockStyle.Fill; _reading.Multiline = true; _reading.ReadOnly = true; _reading.BackColor = Color.FromArgb(24, 37, 58);
            _reading.ForeColor = Color.FromArgb(255, 231, 155); _reading.Font = new Font("Segoe UI", 16f); _reading.TabStop = false;
            layout.Controls.Add(_reading, 0, 3);
            _progress.Dock = DockStyle.Fill; _progress.Margin = new Padding(0, 6, 0, 4); layout.Controls.Add(_progress, 0, 4);
            var meters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            meters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165)); meters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _levelText.Dock = DockStyle.Fill; _level.Dock = DockStyle.Fill; _level.Margin = new Padding(0, 5, 0, 5);
            meters.Controls.Add(_levelText, 0, 0); meters.Controls.Add(_level, 1, 0); layout.Controls.Add(meters, 0, 5);
            _status.Dock = DockStyle.Fill; _status.Padding = new Padding(0, 6, 0, 0); layout.Controls.Add(_status, 0, 6);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
            Configure(_record, "Iniciar cadastro", 160); Configure(_test, "Testar minha voz", 150); Configure(_testOther, "Testar TV/outra voz", 165);
            Configure(_cancel, "Cancelar", 95); Configure(_clear, "Apagar perfil", 120);
            var close = MakeButton("Fechar", 85); close.Click += (s, e) => Close();
            buttons.Controls.AddRange(new Control[] { _record, _test, _testOther, _cancel, _clear, close }); layout.Controls.Add(buttons, 0, 7);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9f), ForeColor = Color.Silver, Text = "Depois do cadastro, teste sua voz e depois a TV/outra pessoa: so a sua deve ser aprovada. F4 e o menu continuam funcionando sem voz." }, 0, 8);

            _record.Click += (s, e) => { if (!_enrolling) { Array.Clear(_phrases, 0, _phrases.Length); _current = 0; _enrolling = true; } StartPhrase(); };
            _test.Click += (s, e) => StartCapture(VoiceCaptureMode.TestOwnVoice);
            _testOther.Click += (s, e) => StartCapture(VoiceCaptureMode.TestOtherVoice);
            _cancel.Click += (s, e) => { _next.Stop(); _voice.CancelVoiceEnrollment(); RefreshState(); };
            _clear.Click += (s, e) =>
            {
                try { _voice.VoiceProfile.Clear(); _session = null; _enrolling = false; SetStatus("Perfil apagado. Cadastre sua voz novamente para usar a escuta protegida.", Color.Khaki); }
                catch (Exception ex) { SetStatus(ex.Message, Color.Salmon); }
                RefreshState();
            };
            _next.Interval = 1300; _next.Tick += (s, e) => { _next.Stop(); if (_enrolling && !IsDisposed) StartPhrase(); };
            _refresh.Interval = 120; _refresh.Tick += (s, e) => RefreshState(); _refresh.Start();
            FormClosed += (s, e) => { _next.Stop(); _voice.CancelVoiceEnrollment(); _refresh.Stop(); _refresh.Dispose(); _next.Dispose(); };
            SetStatus(_voice.VoiceProtectionStatus, Color.LightSkyBlue);
            ShowIdleText();
            RefreshState();
        }

        private static Button MakeButton(string title, int width) { var b = new Button(); Configure(b, title, width); return b; }
        private static void Configure(Button b, string text, int width) { b.Text = text; b.Width = width; b.Height = 34; b.FlatStyle = FlatStyle.Flat; b.Margin = new Padding(0, 2, 8, 2); }
        private void SetStatus(string text, Color color) { _status.Text = text; _status.ForeColor = color; }

        private void ShowIdleText()
        {
            _reading.Text = _voice.VoiceProfile.IsReady
                ? "Sua voz ja esta cadastrada. Use os testes abaixo ou refaca o cadastro se mudou de ambiente/microfone."
                : "Clique em Iniciar cadastro. Cada frase aparece aqui; leia quando a barra comecar a andar.";
        }

        private void StartPhrase()
        {
            if (_current >= _phrases.Length) return;
            _reading.Text = Phrases[_current];
            StartCapture(VoiceCaptureMode.EnrollPhrase);
        }

        private void StartCapture(VoiceCaptureMode mode)
        {
            try
            {
                _session = _voice.BeginVoiceCapture(mode); _reported = false; _progress.Value = 0;
                if (mode != VoiceCaptureMode.EnrollPhrase)
                    _reading.Text = mode == VoiceCaptureMode.TestOwnVoice ? "Fale um comando qualquer, por exemplo:\r\n\"Frida, como esta o transito para o centro?\"" : "Fique em silencio e deixe a TV, um video ou outra pessoa falar por 5 segundos.";
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, Color.Salmon); BootLogger.Log("TreinoVoz: inicio_recusado; " + ex.GetType().Name + "; " + ex.Message);
                if (mode == VoiceCaptureMode.EnrollPhrase) _enrolling = _current > 0;
            }
            RefreshState();
        }

        private void OnCaptureFinished(VoiceEnrollmentResult result)
        {
            if (result.Mode != VoiceCaptureMode.EnrollPhrase)
            {
                SetStatus(result.Message, result.TestPassed ? Color.LightGreen : Color.Salmon);
                ShowIdleText(); _progress.Value = 100;
                return;
            }
            if (result.State == VoiceEnrollmentState.Cancelled) { SetStatus(result.Message, Color.Khaki); return; }
            if (!result.TestPassed || result.Sample == null)
            {
                SetStatus("Frase " + (_current + 1) + ": " + result.Message + "\nClique em \"Gravar frase " + (_current + 1) + "\" para repetir.", Color.Salmon);
                return;
            }
            _phrases[_current] = result.Sample.Embedding;
            int missing = Array.FindIndex(_phrases, x => x == null);
            if (missing >= 0)
            {
                _current = missing;
                SetStatus(result.Message + " Proxima frase em instantes...", Color.LightGreen);
                _reading.Text = "Proxima: " + Phrases[_current];
                _next.Start();
                return;
            }
            // Todas as frases gravadas: confere consistencia e salva.
            var list = _phrases.ToList();
            var summary = SpeakerVoiceProfile.Summarize(list);
            if (summary.WorstIndex >= 0 && summary.LooMean >= 0.55)
            {
                _phrases[summary.WorstIndex] = null; _current = summary.WorstIndex;
                SetStatus("A frase " + (_current + 1) + " ficou diferente das outras (ruido ou outra voz junto). Vamos regravar so ela.", Color.Khaki);
                _reading.Text = "Proxima: " + Phrases[_current];
                _next.Start();
                return;
            }
            try
            {
                var saved = _voice.VoiceProfile.SaveEnrollment(list, _voice.ActiveMicName);
                _enrolling = false; _current = 0;
                SetStatus("Cadastro concluido. Consistencia " + saved.LooMean.ToString("F2") + ", limite " + saved.Threshold.ToString("F2") + ". Agora use os dois testes abaixo.", Color.LightGreen);
                ShowIdleText();
            }
            catch (Exception ex)
            {
                _enrolling = false; Array.Clear(_phrases, 0, _phrases.Length); _current = 0;
                SetStatus(ex.Message, Color.Salmon);
            }
        }

        private void ChooseMicrophone(Control anchor)
        {
            try
            {
                var menu = new ContextMenuStrip();
                foreach (var device in _voice.ListTrainingMicrophones())
                {
                    var selected = device; var item = new ToolStripMenuItem(device.Name) { Checked = device.DeviceId == _voice.ActiveMicDeviceId };
                    item.Click += (s, e) => { _voice.SelectTrainingMicrophone(selected.DeviceId, selected.Name); _session = null; SetStatus("Microfone selecionado. Aguarde o nivel aparecer antes de gravar.", Color.LightSkyBlue); RefreshState(); };
                    menu.Items.Add(item);
                }
                if (menu.Items.Count == 0) menu.Items.Add(new ToolStripMenuItem("Nenhum microfone encontrado") { Enabled = false });
                menu.Closed += (s, e) => menu.Dispose(); menu.Show(anchor, new Point(0, anchor.Height));
            }
            catch (Exception ex) { SetStatus(ex.Message, Color.Salmon); }
        }

        private void RefreshState()
        {
            if (IsDisposed) return;
            bool active = _voice.IsEnrollingVoice;
            var profile = _voice.VoiceProfile;
            _microphone.Text = "Microfone: " + (_voice.ActiveMicName ?? "nao selecionado") + "\nPerfil: " +
                (profile.IsReady ? "cadastrado em " + profile.CreatedAt + (profile.MatchesMicrophone(_voice.ActiveMicName) ? "" : " (outro microfone)") :
                 !SpeakerVoiceProfile.EngineAvailable ? "verificador indisponivel — " + SpeakerVoiceProfile.EngineError :
                 profile.HasLegacyProfile ? "formato antigo, refaca o cadastro" : "ainda nao cadastrado");
            _steps.Text = _enrolling ? "Frase " + (_current + 1) + " de " + _phrases.Length + "   " + string.Join(" ", _phrases.Select(x => x != null ? "●" : "○")) : profile.IsReady ? "Voz cadastrada ✓" : "Cadastro: 0 de " + _phrases.Length;
            bool waiting = _next.Enabled;
            _record.Text = _enrolling ? "Gravar frase " + (_current + 1) : profile.IsReady ? "Refazer cadastro" : "Iniciar cadastro";
            _record.Enabled = !active && !waiting && SpeakerVoiceProfile.EngineAvailable;
            _clear.Enabled = !active && !waiting; _cancel.Enabled = active || waiting;
            _test.Enabled = !active && !waiting && !_enrolling && profile.IsReady; _testOther.Enabled = _test.Enabled;
            double rms = _voice.MicrophoneRms;
            if (_session != null)
            {
                var state = _session.Snapshot();
                if (active)
                {
                    rms = state.Rms;
                    if (state.State == VoiceEnrollmentState.Recording)
                    {
                        _progress.Value = (int)Math.Max(0, Math.Min(100, 100 * state.ElapsedSeconds / Math.Max(1, state.ElapsedSeconds + state.RemainingSeconds)));
                        SetStatus(state.Message + "  (" + Math.Ceiling(state.RemainingSeconds) + " s)", Color.LightSkyBlue);
                    }
                    else SetStatus(state.Message, Color.LightSkyBlue);
                }
                else if (!_reported && _session.Completion.IsCompleted)
                {
                    _reported = true; _progress.Value = 100;
                    OnCaptureFinished(_session.Completion.Result);
                }
            }
            _level.Value = (int)Math.Max(0, Math.Min(100, rms / 90));
            _levelText.Text = rms < 70 ? "Nivel: baixo/silencio" : "Nivel do microfone";
        }
    }
}
