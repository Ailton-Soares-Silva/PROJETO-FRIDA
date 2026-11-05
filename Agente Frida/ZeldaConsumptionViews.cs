// language: C# 5 (.NET Framework 4.8), file: ZeldaConsumptionViews.cs, target: Windows 10/11 x64
// Visoes de consumo real do painel Zelda: lateral "Consumo ao vivo", HUD de telemetria,
// sala "Consumo & Historico", botoes de gerenciamento e teste real de modelo.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FridaAgent
{
    public partial class ZeldaExecutiveLlmDashboard
    {
        private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

        private static Rectangle SidePanelRect { get { return new Rectangle(980, 76, 385, 712); } }
        private static Rectangle HudRect { get { return new Rectangle(14, 510, 952, 278); } }

        private string _selectedKey = null;
        private string _hoverPanelKey = null;
        private int _panelScroll = 0;
        private readonly List<KeyValuePair<Rectangle, string>> _panelRows = new List<KeyValuePair<Rectangle, string>>();

        private Rectangle _btnToggleEnabledRect;
        private Rectangle _btnResetTodayRect;
        private bool _isHoveringToggleBtn = false;
        private bool _isHoveringResetTodayBtn = false;

        // ------------------------------------------------------------------
        // Selecao estavel por chave (a lista muda de ordem/tamanho em tempo real)
        // ------------------------------------------------------------------
        private void SyncSelectionWithKey(List<ModelRuntimeStats> list)
        {
            if (list.Count == 0) { _selectedCharIndex = -1; return; }
            if (_selectedKey != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i].Key, _selectedKey, StringComparison.OrdinalIgnoreCase)) { _selectedCharIndex = i; return; }
                }
            }
            if (_selectedCharIndex < 0 || _selectedCharIndex >= list.Count) _selectedCharIndex = 0;
            _selectedKey = list[_selectedCharIndex].Key;
        }

        private void SelectByIndex(int index)
        {
            List<ModelRuntimeStats> list = GetPanelStats();
            if (index < 0 || index >= list.Count) return;
            _selectedCharIndex = index;
            _selectedKey = list[index].Key;
        }

        private ModelRuntimeStats SelectedStats()
        {
            List<ModelRuntimeStats> list = GetPanelStats();
            SyncSelectionWithKey(list);
            return (_selectedCharIndex >= 0 && _selectedCharIndex < list.Count) ? list[_selectedCharIndex] : null;
        }

        private void SetFeedback(string message, int seconds)
        {
            _testFeedbackMessage = message ?? "";
            _testFeedbackUntil = DateTime.UtcNow.AddSeconds(seconds);
            Invalidate();
        }

        // ------------------------------------------------------------------
        // Interacao
        // ------------------------------------------------------------------
        private bool UpdateExtraHover(Point p)
        {
            _isHoveringToggleBtn = _btnToggleEnabledRect.Contains(p);
            _isHoveringResetTodayBtn = _btnResetTodayRect.Contains(p);
            string oldHover = _hoverPanelKey;
            _hoverPanelKey = null;
            for (int i = 0; i < _panelRows.Count; i++)
            {
                if (_panelRows[i].Key.Contains(p)) { _hoverPanelKey = _panelRows[i].Value; break; }
            }
            if (oldHover != _hoverPanelKey) Invalidate();
            return _hoverPanelKey != null || _isHoveringToggleBtn || _isHoveringResetTodayBtn;
        }

        private bool HandleExtraClick(Point p)
        {
            for (int i = 0; i < _panelRows.Count; i++)
            {
                if (!_panelRows[i].Key.Contains(p)) continue;
                _selectedKey = _panelRows[i].Value;
                SyncSelectionWithKey(GetPanelStats());
                if (_currentRoom == DashboardRoom.Consumption || _currentRoom == DashboardRoom.MeetingRoom) SwitchRoom(DashboardRoom.Overview);
                _testFeedbackMessage = "";
                PlayRetroChime();
                Invalidate();
                return true;
            }

            if (_currentRoom == DashboardRoom.MeetingRoom || _currentRoom == DashboardRoom.Consumption) return false;
            ModelRuntimeStats cur = SelectedStats();
            if (cur == null) return false;

            if (_btnToggleEnabledRect.Contains(p))
            {
                bool disable = !cur.IsDisabled;
                ModelQuotaGuard.SetDisabled(cur.Bucket, cur.ModelId, disable);
                SetFeedback(disable
                    ? cur.ModelId + " desativado: a Frida não vai mais usá-lo até você reativar."
                    : cur.ModelId + " reativado e de volta à fila.", 7);
                UpdateCharacterSlotsForCurrentRoom();
                PlayRetroChime();
                return true;
            }
            if (_btnResetTodayRect.Contains(p))
            {
                DialogResult ok = MessageBox.Show(this,
                    "Zerar a contagem de HOJE de todos os modelos no painel?\n\nIsso não altera a cota real do provedor; só reinicia os contadores locais.",
                    "Zerar contagem", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ok == DialogResult.Yes)
                {
                    ModelQuotaGuard.ResetTodayCounters();
                    SetFeedback("Contadores de hoje zerados.", 5);
                }
                return true;
            }
            return false;
        }

        private void OnSidePanelWheel(object sender, MouseEventArgs e)
        {
            if (!SidePanelRect.Contains(e.Location)) return;
            _panelScroll += e.Delta > 0 ? -2 : 2;
            if (_panelScroll < 0) _panelScroll = 0;
            Invalidate();
        }

        // ------------------------------------------------------------------
        // Teste real de modelo (antes registrava sucesso falso para voz/transcricao)
        // ------------------------------------------------------------------
        private async void ExecuteModelPingTestAsync(ModelRuntimeStats stats)
        {
            if (stats == null || _isTestingNow) return;
            string bucket = (stats.Bucket ?? "").ToLowerInvariant();
            if (bucket.StartsWith("tts") || bucket.StartsWith("stt"))
            {
                SetFeedback("Voz e transcrição são medidas no uso real (fale com a Frida ou use \"Apresentar Holograma e Voz\"). Nada foi consumido.", 9);
                return;
            }

            _isTestingNow = true;
            SetFeedback("Enviando requisição real de teste para " + stats.ModelId + "...", 30);
            string result;
            try
            {
                result = await SendProbeAsync(stats);
            }
            catch (Exception ex)
            {
                result = "Teste falhou: " + TruncateText(ex.Message, 100);
            }
            _isTestingNow = false;
            SetFeedback(result, 10);
            UpdateCharacterSlotsForCurrentRoom();
        }

        private async Task<string> SendProbeAsync(ModelRuntimeStats stats)
        {
            string bucket = (stats.Bucket ?? "").ToLowerInvariant();
            string url;
            string key;
            bool isGoogle = false;
            string tokenField = "max_tokens";

            if (bucket.Contains("groq")) { url = "https://api.groq.com/openai/v1/chat/completions"; key = _settings.groq_api_key; tokenField = "max_completion_tokens"; }
            else if (bucket.Contains("cerebras")) { url = "https://api.cerebras.ai/v1/chat/completions"; key = _settings.cerebras_api_key; tokenField = "max_completion_tokens"; }
            else if (bucket.Contains("openrouter")) { url = "https://openrouter.ai/api/v1/chat/completions"; key = _settings.openrouter_api_key; }
            else if (bucket.Contains("zai")) { url = "https://open.bigmodel.cn/api/paas/v4/chat/completions"; key = _settings.zai_glm_api_key; }
            else if (bucket.Contains("siliconflow") || bucket.EndsWith("_sf")) { url = "https://api.siliconflow.com/v1/chat/completions"; key = _settings.siliconflow_api_key; }
            else { url = "https://generativelanguage.googleapis.com/v1beta/models/" + stats.ModelId + ":generateContent"; key = _settings.api_key; isGoogle = true; }

            key = (key ?? "").Trim();
            if (key.Length == 0) return "Sem chave de API configurada para " + stats.ProviderLabel + ".";

            string json;
            if (isGoogle)
            {
                json = "{\"contents\":[{\"parts\":[{\"text\":\"Responda apenas: ok\"}]}],\"generationConfig\":{\"maxOutputTokens\":16}}";
            }
            else
            {
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["model"] = stats.ModelId;
                body["messages"] = new object[] { new Dictionary<string, object> { { "role", "user" }, { "content", "Responda apenas: ok" } } };
                body[tokenField] = 32;
                json = _json.Serialize(body);
            }

            using (HttpClient client = new HttpClient())
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                client.Timeout = TimeSpan.FromSeconds(25);
                if (isGoogle) req.Headers.TryAddWithoutValidation("x-goog-api-key", key);
                else req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                if (bucket.Contains("openrouter")) req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://localhost/agente-frida");
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                using (HttpResponseMessage resp = await client.SendAsync(req))
                {
                    string text = await resp.Content.ReadAsStringAsync();
                    sw.Stop();
                    ModelQuotaGuard.RecordHttpResponse(stats.Bucket, stats.ModelId, sw.ElapsedMilliseconds, resp, text);
                    if (!resp.IsSuccessStatusCode)
                    {
                        return "Falha HTTP " + (int)resp.StatusCode + ": " + TruncateText(stats.LastErrorReason, 120);
                    }
                    string limits = stats.DescribeProviderLimits();
                    return string.Format("OK em {0:0} ms. {1}", sw.ElapsedMilliseconds,
                        limits.Length > 0 ? limits : "(este provedor não envia limites nos cabeçalhos)");
                }
            }
        }

        // ------------------------------------------------------------------
        // Utilitarios de desenho
        // ------------------------------------------------------------------
        private static string Num(long v) { return v.ToString("N0", Br); }

        private static string Ago(DateTime local)
        {
            if (local == DateTime.MinValue) return "nunca";
            TimeSpan t = DateTime.Now - local;
            if (t.TotalSeconds < 60) return "agora";
            if (t.TotalMinutes < 60) return (int)t.TotalMinutes + " min atrás";
            if (t.TotalHours < 24) return (int)t.TotalHours + " h atrás";
            return local.ToString("dd/MM HH:mm");
        }

        private static string Span(TimeSpan t)
        {
            if (t.TotalHours >= 1) return string.Format("{0}h{1:00}m", (int)t.TotalHours, t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format("{0}m{1:00}s", (int)t.TotalMinutes, t.Seconds);
            return Math.Max(1, (int)Math.Ceiling(t.TotalSeconds)) + "s";
        }

        private static string ShortName(string modelId)
        {
            string s = modelId ?? "";
            int i = s.LastIndexOf('/');
            return i >= 0 && i < s.Length - 1 ? s.Substring(i + 1) : s;
        }

        private static Color StateColor(ModelRuntimeStats s)
        {
            if (s.IsActiveNow) return Color.FromArgb(70, 245, 130);
            if (s.IsDisabled) return Color.FromArgb(125, 130, 145);
            if (s.IsCooldownActive()) return Color.FromArgb(255, 75, 75);
            return Color.FromArgb(255, 205, 50);
        }

        private static string StateText(ModelRuntimeStats s)
        {
            if (!string.IsNullOrEmpty(s.TeamRoleId))
            {
                if (s.IsDisabled) return "DESATIVADO PELO OPERADOR";
                if (s.TeamLocation == DashboardRoom.BreakRoom) return "COTA EXCEDIDA · volta em " + Span(s.GetCooldownRemaining());
                if (!s.TeamLocation.HasValue) return "TEMPORARIAMENTE INDISPONIVEL";
                if (s.TeamLocation == DashboardRoom.GameRoom) return "OCIOSO HA PELO MENOS 2 MINUTOS";
                if (s.TeamLocation == DashboardRoom.MeetingRoom) return "EM REUNIAO · " + s.TeamStage;
                if (s.TeamLocation == DashboardRoom.Office) return "TRABALHANDO · " + s.TeamStage;
                return "DISPONIVEL NO HALL";
            }
            if (s.IsActiveNow) return "EM USO AGORA";
            if (s.IsDisabled) return "DESATIVADO PELO OPERADOR";
            if (s.IsCooldownActive()) return "SEM COTA · libera em " + Span(s.GetCooldownRemaining());
            return "PRONTO PARA USO";
        }

        /// <summary>Fracao do limite mais apertado entre todas as janelas (0..1). known=false quando nenhum limite e conhecido.</summary>
        private static double DailyUse(ModelRuntimeStats s, out bool known)
        {
            UsageLimitLine peak = s.PeakLimitLine();
            if (peak != null) { known = true; return s.IsCooldownActive() ? 1.0 : peak.Fraction; }
            known = s.IsCooldownActive();
            return known ? 1.0 : 0.0;
        }

        /// <summary>Numero curto para celulas estreitas: 950 · 12,4 mil · 1,2 mi.</summary>
        private static string Compact(long v)
        {
            if (v >= 1000000) return (v / 1000000.0).ToString(v >= 10000000 ? "0" : "0.#", Br) + " mi";
            if (v >= 10000) return (v / 1000.0).ToString(v >= 100000 ? "0" : "0.#", Br) + " mil";
            return Num(v);
        }

        private static Color FracColor(double frac)
        {
            return frac >= 0.9 ? Color.FromArgb(240, 70, 70) : frac >= 0.6 ? Color.FromArgb(245, 185, 45) : Color.FromArgb(60, 210, 115);
        }

        private static string LimitSourceText(ModelRuntimeStats s, List<UsageLimitLine> lines)
        {
            bool api = false, cfg = false;
            foreach (UsageLimitLine l in lines) { if (l.Source == "API") api = true; else if (l.Source == "config") cfg = true; }
            if (api) return "limites da API · lidos " + Ago(s.RateLimitsAtLocal) + (cfg ? " · + config" : "");
            if (cfg) return "limites do config (model_limits) · uso contado localmente";
            if ((s.Bucket ?? "") == "tts_edge") return "serviço gratuito sem cota publicada · uso contado localmente";
            return "provedor não informa limites · uso contado localmente";
        }

        private static void FillBar(Graphics g, Rectangle r, double frac, bool known)
        {
            using (SolidBrush back = new SolidBrush(Color.FromArgb(28, 38, 60))) g.FillRectangle(back, r);
            if (!known)
            {
                using (HatchBrush hb = new HatchBrush(HatchStyle.LightUpwardDiagonal, Color.FromArgb(55, 75, 105), Color.FromArgb(28, 38, 60)))
                    g.FillRectangle(hb, r);
            }
            else if (frac > 0)
            {
                Color c = frac >= 0.9 ? Color.FromArgb(240, 70, 70) : frac >= 0.6 ? Color.FromArgb(245, 185, 45) : Color.FromArgb(60, 210, 115);
                using (SolidBrush fb = new SolidBrush(c)) g.FillRectangle(fb, r.X, r.Y, Math.Max(1, (int)(r.Width * frac)), r.Height);
            }
            using (Pen p = new Pen(Color.FromArgb(85, 110, 150))) g.DrawRectangle(p, r);
        }

        private static void Txt(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            using (SolidBrush b = new SolidBrush(c))
            using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(s ?? "", f, b, r, sf);
            }
        }

        private void DrawFramedPanel(Graphics g, Rectangle r)
        {
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(12, 18, 36))) g.FillRectangle(bg, r);
            DrawZeldaOrnamentalBorder(g, r);
        }

        // ------------------------------------------------------------------
        // Etiqueta sob cada boneco: nome real do modelo (antes era fixa por sprite)
        // ------------------------------------------------------------------
        private void DrawCharacterMiniTag(Graphics g, int cx, int cy, ModelRuntimeStats stats, bool isSelected)
        {
            if (stats == null) return;
            string label = TruncateText(string.IsNullOrEmpty(stats.TeamRoleId) ? ShortName(stats.ModelId) : stats.CharacterTitle, _currentRoom == DashboardRoom.Overview ? 14 : 24);
            if (_currentRoom == DashboardRoom.Overview && !string.IsNullOrEmpty(stats.TeamRoleId))
            {
                string[] roles = { "Secretary", "Manager", "Technician", "Analyst", "Assistant", "Hearing", "Voice" };
                string[] names = { "Secretaria", "Gestor", "Tecnico", "Analista", "Assistente", "Escuta", "Voz" };
                int index = Array.IndexOf(roles, stats.TeamRoleId);
                if (index >= 0) label = names[index];
            }
            using (Font f = new Font("Segoe UI", 7.5f, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(label, f);
                Rectangle box = new Rectangle(cx - (int)(sz.Width / 2) - 4, cy, (int)sz.Width + 8, (int)sz.Height);
                using (SolidBrush bg = new SolidBrush(isSelected ? Color.FromArgb(230, 25, 45, 80) : Color.FromArgb(190, 15, 20, 35)))
                using (Pen border = new Pen(isSelected ? Color.FromArgb(255, 215, 0) : StateColor(stats)))
                {
                    g.FillRectangle(bg, box);
                    g.DrawRectangle(border, box);
                }
                using (SolidBrush tb = new SolidBrush(isSelected ? Color.FromArgb(255, 235, 140) : Color.FromArgb(220, 230, 245)))
                    g.DrawString(label, f, tb, box.X + 4, box.Y);
            }
        }

        // ------------------------------------------------------------------
        // Lateral: consumo ao vivo de todos os modelos
        // ------------------------------------------------------------------
        private void DrawLiveConsumptionPanel(Graphics g)
        {
            Rectangle r = SidePanelRect;
            DrawFramedPanel(g, r);
            List<ModelRuntimeStats> list = GetPanelStats();
            SyncSelectionWithKey(list);

            using (Font head = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (Font hint = new Font("Segoe UI", 7.5f))
            {
                Txt(g, _currentRoom == DashboardRoom.Consumption ? "CONSUMO AO VIVO · HOJE" : "EQUIPE E SERVICOS · AO VIVO", head, Color.FromArgb(255, 223, 118), new RectangleF(r.X + 14, r.Y + 10, r.Width - 28, 20));
                Txt(g, "Clique para selecionar. Consumo por modelo na aba [7].", hint, Color.FromArgb(145, 170, 205), new RectangleF(r.X + 14, r.Y + 30, r.Width - 28, 16));
            }

            _panelRows.Clear();
            const int rowH = 46;
            int top = r.Y + 52;
            int capacity = Math.Max(1, (r.Bottom - 10 - top) / rowH);
            _panelScroll = Math.Min(_panelScroll, Math.Max(0, list.Count - capacity));

            using (Font nameF = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (Font smallF = new Font("Segoe UI", 7.25f))
            {
                for (int i = _panelScroll, row = 0; i < list.Count && row < capacity; i++, row++)
                {
                    ModelRuntimeStats s = list[i];
                    Rectangle rr = new Rectangle(r.X + 10, top + row * rowH, r.Width - 20, rowH - 4);
                    _panelRows.Add(new KeyValuePair<Rectangle, string>(rr, s.Key));
                    bool sel = string.Equals(s.Key, _selectedKey, StringComparison.OrdinalIgnoreCase);
                    bool hov = string.Equals(s.Key, _hoverPanelKey, StringComparison.OrdinalIgnoreCase);

                    using (SolidBrush bg = new SolidBrush(sel ? Color.FromArgb(34, 56, 98) : hov ? Color.FromArgb(24, 38, 66) : Color.FromArgb(17, 26, 46)))
                        g.FillRectangle(bg, rr);
                    if (sel) using (Pen gp = new Pen(Color.FromArgb(255, 215, 60))) g.DrawRectangle(gp, rr);

                    Color sc = StateColor(s);
                    using (SolidBrush dot = new SolidBrush(sc)) g.FillEllipse(dot, rr.X + 6, rr.Y + 8, 9, 9);

                    int rightW = 92;
                    Txt(g, (string.IsNullOrEmpty(s.TeamRoleId) ? ShortName(s.ModelId) : s.CharacterTitle), nameF, s.IsDisabled ? Color.FromArgb(140, 145, 160) : Color.White,
                        new RectangleF(rr.X + 20, rr.Y + 3, rr.Width - rightW - 24, 16));
                    Txt(g, string.IsNullOrEmpty(s.TeamRoleId) ? s.ProviderLabel + " · " + s.FunctionLabel : StateText(s), smallF, Color.FromArgb(145, 172, 210),
                        new RectangleF(rr.X + 20, rr.Y + 18, rr.Width - rightW - 24, 14));

                    using (StringFormat right = new StringFormat(StringFormatFlags.NoWrap))
                    using (SolidBrush vb = new SolidBrush(s.TotalRequestsToday > 0 ? Color.FromArgb(140, 255, 170) : Color.FromArgb(170, 175, 190)))
                    using (SolidBrush fb = new SolidBrush(s.FailuresToday > 0 ? Color.FromArgb(255, 140, 130) : Color.FromArgb(145, 172, 210)))
                    {
                        right.Alignment = StringAlignment.Far;
                        g.DrawString(Num(s.TotalRequestsToday) + " req", nameF, vb, new RectangleF(rr.Right - rightW - 4, rr.Y + 3, rightW, 16), right);
                        // Prioridade: sem cota > janela mais perto do limite > falhas > tokens do dia
                        UsageLimitLine peak = s.PeakLimitLine();
                        string sub = s.IsCooldownActive() ? "volta " + Span(s.GetCooldownRemaining())
                                   : peak != null ? string.Format("{0} {1:0}%", peak.Label, peak.Fraction * 100)
                                   : s.FailuresToday > 0 ? Num(s.FailuresToday) + " falhas"
                                   : s.Bucket == "tts_edge" ? "grátis"
                                   : Compact(s.TokensInToday + s.TokensOutToday) + " tok";
                        using (SolidBrush pb = new SolidBrush(peak != null && !s.IsCooldownActive() ? FracColor(peak.Fraction) : Color.White))
                            g.DrawString(sub, smallF, s.IsCooldownActive() || (peak == null && s.FailuresToday > 0) ? fb : peak != null ? pb : vb,
                                new RectangleF(rr.Right - rightW - 4, rr.Y + 18, rightW, 14), right);
                    }

                    bool known;
                    double frac = DailyUse(s, out known);
                    FillBar(g, new Rectangle(rr.X + 20, rr.Bottom - 8, rr.Width - 26, 4), frac, known);
                }
            }

            if (list.Count > capacity)
            {
                using (Font f = new Font("Segoe UI", 7f))
                    Txt(g, string.Format("{0}–{1} de {2}", _panelScroll + 1, Math.Min(list.Count, _panelScroll + capacity), list.Count), f,
                        Color.FromArgb(145, 170, 205), new RectangleF(r.Right - 90, r.Y + 30, 80, 14));
            }
        }

        // ------------------------------------------------------------------
        // HUD do modelo selecionado
        // ------------------------------------------------------------------
        private void DrawStandardZeldaHud(Graphics g)
        {
            Rectangle hud = HudRect;
            DrawFramedPanel(g, hud);
            LayoutHudButtonRow(hud);

            ModelRuntimeStats s = SelectedStats();
            if (s == null)
            {
                using (Font f = new Font("Segoe UI", 10f, FontStyle.Italic))
                    Txt(g, "Nenhum modelo registrado ainda. Configure as chaves de API ou fale com a Frida para começar a medir.", f,
                        Color.FromArgb(170, 190, 220), new RectangleF(hud.X + 20, hud.Y + 24, hud.Width - 40, 20));
                DrawZeldaButton(g, _btnConfigKeysRect, "[⚙] Chaves API", _isHoveringConfigBtn, Color.FromArgb(40, 90, 80));
                return;
            }

            Rectangle portrait = new Rectangle(hud.X + 16, hud.Y + 18, 84, 84);
            using (SolidBrush pb = new SolidBrush(Color.FromArgb(20, 32, 60))) g.FillRectangle(pb, portrait);
            using (Pen pg = new Pen(Color.FromArgb(218, 165, 32), 2)) g.DrawRectangle(pg, portrait);
            DrawDetailedPixelPortrait(g, portrait, s.SpriteKey);

            int rank = ModelQuotaGuard.GetPreferenceRank(s.Bucket, s.ModelId);
            using (Font chipF = new Font("Segoe UI", 7.5f, FontStyle.Bold))
            {
                if (rank != int.MaxValue)
                    Txt(g, "★ PREFERIDO #" + (rank + 1), chipF, Color.FromArgb(255, 220, 80), new RectangleF(portrait.X, portrait.Bottom + 6, 110, 14));
                if (s.IsDisabled)
                    Txt(g, "✕ DESATIVADO", chipF, Color.FromArgb(200, 140, 150), new RectangleF(portrait.X, portrait.Bottom + 22, 110, 14));
            }

            int x = portrait.Right + 18;
            int w = hud.Right - x - 16;
            using (Font titleF = new Font("Segoe UI", 11.5f, FontStyle.Bold))
            using (Font monoF = new Font("Consolas", 9f))
            using (Font stF = new Font("Segoe UI", 9f, FontStyle.Bold))
            {
                Txt(g, (string.IsNullOrEmpty(s.TeamRoleId) ? s.FunctionLabel : s.CharacterTitle) + " · " + s.ProviderLabel, titleF, Color.FromArgb(255, 225, 110), new RectangleF(x, hud.Y + 12, w, 22));
                Txt(g, s.ModelId, monoF, Color.FromArgb(165, 205, 240), new RectangleF(x, hud.Y + 34, w, 16));
                Color sc = StateColor(s);
                using (SolidBrush dot = new SolidBrush(sc)) g.FillEllipse(dot, x, hud.Y + 57, 9, 9);
                Txt(g, StateText(s), stF, sc, new RectangleF(x + 14, hud.Y + 53, (w - 12) / 2 - 14, 18));
            }

            // Esquerda: metricas reais do dia. Direita: limite x uso atual por janela.
            int colW = (w - 12) / 2;
            int x2 = x + colW + 12;
            int y = hud.Y + 80;
            const int lh = 21;
            string[][] left = new string[][]
            {
                new string[] { "Hoje", string.Format("{0} ok · {1} falhas · {2} bloqueios 429", Num(s.TotalRequestsToday), Num(s.FailuresToday), Num(s.RateLimitedToday)) },
                new string[] { "Tokens hoje", string.Format("{0} entrada · {1} saída", Num(s.TokensInToday), Num(s.TokensOutToday)) },
                new string[] { "Acumulado", string.Format("{0} req · {1} tokens", Num(s.TotalRequestsAllTime), Num(s.TokensInAllTime + s.TokensOutAllTime)) },
                new string[] { "Latência", s.LastLatencyMs > 0 ? string.Format("{0:0} ms (média {1:0} ms)", s.LastLatencyMs, s.AvgLatencyMs) : "—" },
                new string[] { "Último uso", Ago(s.LastUsedLocal) }
            };

            List<UsageLimitLine> lines = s.GetLimitLines();
            using (Font lf = new Font("Segoe UI", 8.75f))
            using (Font vf = new Font("Segoe UI", 8.75f, FontStyle.Bold))
            using (Font hf = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (Font sf = new Font("Segoe UI", 7.25f))
            {
                for (int i = 0; i < left.Length; i++)
                {
                    Txt(g, left[i][0] + ":", lf, Color.FromArgb(145, 172, 210), new RectangleF(x, y + i * lh, 90, 18));
                    Txt(g, left[i][1], vf, Color.White, new RectangleF(x + 90, y + i * lh, colW - 92, 18));
                }

                Txt(g, "LIMITE × USO ATUAL", hf, Color.FromArgb(255, 223, 118), new RectangleF(x2, hud.Y + 54, 128, 16));
                Txt(g, s.QuotaRefreshPending ? "aguardando nova resposta da API" : LimitSourceText(s, lines), sf, Color.FromArgb(145, 172, 210),
                    new RectangleF(x2 + 128, hud.Y + 56, colW - 128, 14));

                const int lr = 17;
                int ly = y - 6;
                int maxRows = Math.Min(lines.Count, 7);
                for (int i = 0; i < maxRows; i++)
                {
                    UsageLimitLine l = lines[i];
                    int ry = ly + i * lr;
                    Txt(g, l.Label, lf, Color.FromArgb(145, 172, 210), new RectangleF(x2, ry, 74, 16));
                    FillBar(g, new Rectangle(x2 + 76, ry + 4, colW - 76 - 132, 9), l.Fraction, l.HasLimit);
                    string val = l.HasLimit
                        ? string.Format("{0} / {1}  {2:0}%", Compact(l.Used), Compact(l.Limit), l.Fraction * 100)
                        : Compact(l.Used) + " · sem limite";
                    using (StringFormat rf = new StringFormat(StringFormatFlags.NoWrap))
                    using (SolidBrush vb = new SolidBrush(l.HasLimit ? (l.Fraction >= 0.6 ? FracColor(l.Fraction) : Color.White) : Color.FromArgb(170, 180, 200)))
                    {
                        rf.Alignment = StringAlignment.Far;
                        g.DrawString(val, vf, vb, new RectangleF(x2 + colW - 130, ry, 130, 16), rf);
                    }
                }

                if (!string.IsNullOrEmpty(s.LastErrorReason))
                {
                    using (SolidBrush eb = new SolidBrush(Color.FromArgb(255, 150, 140)))
                        g.DrawString("Último erro (" + Ago(s.LastErrorLocal) + "): " + TruncateText(s.LastErrorReason, 140), lf, eb,
                            new RectangleF(x, y + lh * 5 + 2, colW, 30));
                }
            }

            DrawZeldaButton(g, _btnTestRect, _isTestingNow ? "Testando..." : "[►] Testar de Verdade", _isHoveringTestBtn, Color.FromArgb(30, 80, 150));
            DrawZeldaButton(g, _btnResetCooldownRect, "[↻] Atualizar Cota", _isHoveringResetBtn, Color.FromArgb(140, 60, 30));
            DrawZeldaButton(g, _btnSetDefaultRect, rank == int.MaxValue ? "[★] Tornar Preferido" : "[☆] Tirar Preferência", _isHoveringDefaultBtn, Color.FromArgb(100, 80, 20));
            DrawZeldaButton(g, _btnToggleEnabledRect, s.IsDisabled ? "[✓] Ativar" : "[✕] Desativar", _isHoveringToggleBtn, s.IsDisabled ? Color.FromArgb(30, 110, 60) : Color.FromArgb(110, 35, 45));
            DrawZeldaButton(g, _btnResetTodayRect, "[0] Zerar Hoje", _isHoveringResetTodayBtn, Color.FromArgb(60, 60, 95));
            DrawZeldaButton(g, _btnConfigKeysRect, "[⚙] Chaves API", _isHoveringConfigBtn, Color.FromArgb(40, 90, 80));

            if (!string.IsNullOrEmpty(_testFeedbackMessage) && DateTime.UtcNow < _testFeedbackUntil)
            {
                Rectangle fr = new Rectangle(hud.X + 16, _btnTestRect.Y - 26, hud.Width - 32, 22);
                using (SolidBrush fbg = new SolidBrush(Color.FromArgb(235, 22, 32, 58))) g.FillRectangle(fbg, fr);
                using (Font ff = new Font("Segoe UI", 9f, FontStyle.Bold))
                    Txt(g, _testFeedbackMessage, ff, Color.FromArgb(255, 230, 120), new RectangleF(fr.X + 6, fr.Y + 3, fr.Width - 12, 18));
            }
        }

        private void LayoutHudButtonRow(Rectangle hud)
        {
            const int gap = 8;
            int bw = (hud.Width - 32 - gap * 5) / 6;
            int by = hud.Bottom - 46;
            Rectangle[] r = new Rectangle[6];
            for (int i = 0; i < 6; i++) r[i] = new Rectangle(hud.X + 16 + i * (bw + gap), by, bw, 32);
            _btnTestRect = r[0];
            _btnResetCooldownRect = r[1];
            _btnSetDefaultRect = r[2];
            _btnToggleEnabledRect = r[3];
            _btnResetTodayRect = r[4];
            _btnConfigKeysRect = r[5];
        }

        // ------------------------------------------------------------------
        // Tabela: limite x uso atual de todos os modelos (min / hora / dia)
        // ------------------------------------------------------------------
        private void DrawLimitsTable(Graphics g, Rectangle p, List<ModelRuntimeStats> list, int rows, int rowH)
        {
            Color gold = Color.FromArgb(255, 223, 118);
            Color dim = Color.FromArgb(145, 172, 210);
            string[] codes = { "rpm", "rph", "rpd", "tpm", "tph", "tpd" };
            string[] heads = { "Req/min", "Req/hora", "Req/dia", "Tokens/min", "Tokens/hora", "Tokens/dia" };
            const int modelW = 250, srcW = 92;
            int colW = (p.Width - 32 - modelW - srcW) / codes.Length;

            using (Font head = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (Font small = new Font("Segoe UI", 7.5f))
            using (Font cell = new Font("Segoe UI", 8.25f))
            using (Font cellB = new Font("Segoe UI", 8.25f, FontStyle.Bold))
            using (StringFormat center = new StringFormat(StringFormatFlags.NoWrap))
            {
                center.Alignment = StringAlignment.Center;
                center.Trimming = StringTrimming.EllipsisCharacter;
                Txt(g, "LIMITE × USO ATUAL · uso / limite por janela", head, gold, new RectangleF(p.X + 16, p.Y + 10, 520, 20));
                Txt(g, "Verde < 60% · amarelo < 90% · vermelho ≥ 90% · \"—\" = provedor não informa (defina em model_limits no config)", small, dim,
                    new RectangleF(p.X + 380, p.Y + 13, p.Width - 396, 14));

                int hy = p.Y + 34;
                Txt(g, "Modelo", small, dim, new RectangleF(p.X + 16, hy, modelW, 14));
                using (SolidBrush hb = new SolidBrush(dim))
                    for (int i = 0; i < heads.Length; i++)
                        g.DrawString(heads[i], small, hb, new RectangleF(p.X + 16 + modelW + i * colW, hy, colW, 14), center);
                Txt(g, "Fonte", small, dim, new RectangleF(p.Right - 16 - srcW, hy, srcW, 14));

                if (list.Count == 0)
                {
                    Txt(g, "Nenhum modelo com chave configurada.", cell, dim, new RectangleF(p.X + 16, hy + 18, 400, 18));
                    return;
                }

                for (int r = 0; r < rows && r < list.Count; r++)
                {
                    ModelRuntimeStats s = list[r];
                    int ry = hy + 18 + r * rowH;
                    if (r % 2 == 1) using (SolidBrush zb = new SolidBrush(Color.FromArgb(18, 28, 50))) g.FillRectangle(zb, p.X + 10, ry - 1, p.Width - 20, rowH);
                    using (SolidBrush dot = new SolidBrush(StateColor(s))) g.FillEllipse(dot, p.X + 16, ry + 5, 7, 7);
                    Txt(g, s.ProviderLabel + " · " + ShortName(s.ModelId), cellB, s.IsDisabled ? Color.FromArgb(140, 145, 160) : Color.White,
                        new RectangleF(p.X + 28, ry, modelW - 14, 16));

                    List<UsageLimitLine> lines = s.GetLimitLines();
                    bool api = false, cfg = false;
                    for (int i = 0; i < codes.Length; i++)
                    {
                        UsageLimitLine line = null;
                        foreach (UsageLimitLine x in lines) if (x.Code == codes[i]) { line = x; break; }
                        if (line == null) continue;
                        if (line.Source == "API") api = true; else if (line.Source == "config") cfg = true;
                        RectangleF cr = new RectangleF(p.X + 16 + modelW + i * colW, ry, colW, 16);
                        string txt = Compact(line.Used) + " / " + (line.HasLimit ? Compact(line.Limit) : "—");
                        Color tc = line.HasLimit ? (line.Fraction >= 0.6 ? FracColor(line.Fraction) : Color.White) : Color.FromArgb(120, 135, 160);
                        using (SolidBrush tb = new SolidBrush(tc)) g.DrawString(txt, line.HasLimit ? cellB : cell, tb, cr, center);
                        if (line.HasLimit)
                            FillBar(g, new Rectangle((int)cr.X + 10, ry + 15, colW - 20, 3), line.Fraction, true);
                    }
                    string src = api ? "API · " + Ago(s.RateLimitsAtLocal) : cfg ? "config" : "local";
                    Txt(g, src, small, dim, new RectangleF(p.Right - 16 - srcW, ry + 2, srcW, 14));
                }
                if (list.Count > rows)
                    Txt(g, "+" + (list.Count - rows) + " modelos (role a lista lateral)", small, dim, new RectangleF(p.X + 16, p.Bottom - 16, 300, 14));
            }
        }

        // ------------------------------------------------------------------
        // Sala [6]: Consumo & Historico
        // ------------------------------------------------------------------
        private void DrawConsumptionRoom(Graphics g, Rectangle area)
        {
            // Botoes do HUD nao existem nesta sala
            _btnTestRect = _btnResetCooldownRect = _btnSetDefaultRect = _btnToggleEnabledRect = _btnResetTodayRect = _btnConfigKeysRect = Rectangle.Empty;

            List<ModelRuntimeStats> list = ModelQuotaGuard.GetAllStats();
            const int limitRowH = 19;
            int limitRows = Math.Min(Math.Max(list.Count, 1), 11);
            Rectangle a = new Rectangle(area.X, area.Y, area.Width, 172);
            Rectangle l = new Rectangle(area.X, a.Bottom + 8, area.Width, 58 + limitRows * limitRowH);
            Rectangle b = new Rectangle(area.X, l.Bottom + 8, area.Width, 136);
            Rectangle c = new Rectangle(area.X, b.Bottom + 8, area.Width, Math.Max(80, area.Bottom - b.Bottom - 8));
            DrawFramedPanel(g, a);
            DrawFramedPanel(g, l);
            DrawFramedPanel(g, b);
            DrawFramedPanel(g, c);
            DrawLimitsTable(g, l, list, limitRows, limitRowH);

            using (Font head = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (Font cell = new Font("Segoe UI", 8.75f))
            using (Font cellB = new Font("Segoe UI", 8.75f, FontStyle.Bold))
            using (Font small = new Font("Segoe UI", 7.5f))
            {
                Color gold = Color.FromArgb(255, 223, 118);
                Color dim = Color.FromArgb(145, 172, 210);

                // A) Resumo por provedor
                Txt(g, "RESUMO POR PROVEDOR · HOJE", head, gold, new RectangleF(a.X + 16, a.Y + 10, 400, 20));
                string[] cols = { "Provedor", "Modelos", "Requisições", "Falhas", "Tokens entrada", "Tokens saída", "Sem cota" };
                int[] cx = { 16, 210, 290, 400, 480, 610, 740 };
                for (int i = 0; i < cols.Length; i++) Txt(g, cols[i], small, dim, new RectangleF(a.X + cx[i], a.Y + 36, 130, 14));

                SortedDictionary<string, long[]> prov = new SortedDictionary<string, long[]>();
                for (int i = 0; i < list.Count; i++)
                {
                    ModelRuntimeStats s = list[i];
                    long[] t;
                    if (!prov.TryGetValue(s.ProviderLabel, out t)) { t = new long[6]; prov[s.ProviderLabel] = t; }
                    t[0]++;
                    t[1] += s.TotalRequestsToday;
                    t[2] += s.FailuresToday;
                    t[3] += s.TokensInToday;
                    t[4] += s.TokensOutToday;
                    if (s.IsCooldownActive()) t[5]++;
                }
                int ry = a.Y + 54;
                long[] tot = new long[6];
                foreach (KeyValuePair<string, long[]> kv in prov)
                {
                    if (ry > a.Bottom - 40) break;
                    string[] vals = { kv.Key, Num(kv.Value[0]), Num(kv.Value[1]), Num(kv.Value[2]), Num(kv.Value[3]), Num(kv.Value[4]), Num(kv.Value[5]) };
                    for (int i = 0; i < vals.Length; i++)
                    {
                        Color col = i == 3 && kv.Value[2] > 0 ? Color.FromArgb(255, 140, 130) : i == 6 && kv.Value[5] > 0 ? Color.FromArgb(255, 110, 110) : Color.White;
                        Txt(g, vals[i], i == 0 ? cellB : cell, col, new RectangleF(a.X + cx[i], ry, 128, 18));
                    }
                    for (int k = 0; k < 6; k++) tot[k] += kv.Value[k];
                    ry += 20;
                }
                using (Pen sep = new Pen(Color.FromArgb(70, 95, 135))) g.DrawLine(sep, a.X + 16, a.Bottom - 32, a.Right - 16, a.Bottom - 32);
                string[] totals = { "TOTAL", Num(tot[0]), Num(tot[1]), Num(tot[2]), Num(tot[3]), Num(tot[4]), Num(tot[5]) };
                for (int i = 0; i < totals.Length; i++) Txt(g, totals[i], cellB, gold, new RectangleF(a.X + cx[i], a.Bottom - 26, 128, 18));

                // B) Ultimos 7 dias
                Txt(g, "ÚLTIMOS 7 DIAS · requisições de todas as funções", head, gold, new RectangleF(b.X + 16, b.Y + 10, 500, 20));
                long[] dayReq = new long[7];
                long[] dayTok = new long[7];
                string[] dayKey = new string[7];
                for (int d = 0; d < 7; d++) dayKey[d] = DateTime.Now.Date.AddDays(d - 6).ToString("yyyy-MM-dd");
                for (int i = 0; i < list.Count; i++)
                {
                    ModelRuntimeStats s = list[i];
                    dayReq[6] += s.TotalRequestsToday;
                    dayTok[6] += s.TokensInToday + s.TokensOutToday;
                    if (s.History == null) continue;
                    for (int h = 0; h < s.History.Count; h++)
                    {
                        int idx = Array.IndexOf(dayKey, s.History[h].date);
                        if (idx >= 0 && idx < 6)
                        {
                            dayReq[idx] += s.History[h].requests;
                            dayTok[idx] += s.History[h].tokens_in + s.History[h].tokens_out;
                        }
                    }
                }
                long max = 1;
                for (int d = 0; d < 7; d++) max = Math.Max(max, dayReq[d]);
                int chartTop = b.Y + 38, chartH = b.Height - 76;
                int slot = (b.Width - 48) / 7;
                using (StringFormat center = new StringFormat())
                {
                    center.Alignment = StringAlignment.Center;
                    for (int d = 0; d < 7; d++)
                    {
                        int bx = b.X + 24 + d * slot;
                        int bh = (int)(chartH * (dayReq[d] / (double)max));
                        Rectangle bar = new Rectangle(bx + slot / 4, chartTop + chartH - bh, slot / 2, Math.Max(bh, 1));
                        using (SolidBrush bb = new SolidBrush(d == 6 ? Color.FromArgb(80, 200, 255) : Color.FromArgb(60, 130, 200))) g.FillRectangle(bb, bar);
                        using (SolidBrush tb = new SolidBrush(Color.White))
                            g.DrawString(Num(dayReq[d]), cellB, tb, new RectangleF(bx, bar.Y - 16, slot, 16), center);
                        using (SolidBrush lb = new SolidBrush(dim))
                        {
                            DateTime dt = DateTime.Now.Date.AddDays(d - 6);
                            g.DrawString(d == 6 ? "hoje" : dt.ToString("ddd dd/MM", Br), small, lb, new RectangleF(bx, chartTop + chartH + 4, slot, 14), center);
                            g.DrawString(Num(dayTok[d]) + " tok", small, lb, new RectangleF(bx, chartTop + chartH + 18, slot, 14), center);
                        }
                    }
                }

                // C) Ultimas chamadas
                Txt(g, "ÚLTIMAS CHAMADAS (registro real de cada requisição)", head, gold, new RectangleF(c.X + 16, c.Y + 10, 600, 20));
                string[] ecols = { "Hora", "Provedor · modelo", "Resultado", "Latência", "Tokens", "Detalhe" };
                int[] ex = { 16, 90, 390, 480, 560, 650 };
                for (int i = 0; i < ecols.Length; i++) Txt(g, ecols[i], small, dim, new RectangleF(c.X + ex[i], c.Y + 34, 200, 14));
                int lines = Math.Max(1, (c.Height - 60) / 18);
                List<UsageEvent> evs = ModelQuotaGuard.GetRecentEvents(lines);
                int ey = c.Y + 50;
                if (evs.Count == 0)
                {
                    Txt(g, "Nenhuma chamada registrada ainda.", cell, dim, new RectangleF(c.X + 16, ey, 400, 18));
                }
                for (int i = 0; i < evs.Count; i++)
                {
                    UsageEvent e = evs[i];
                    string hora = e.at.Length >= 19 ? e.at.Substring(11, 8) : e.at;
                    string res = e.ok ? "OK" : (e.code > 0 ? "HTTP " + e.code : "falha de rede");
                    Color rc = e.ok ? Color.FromArgb(120, 240, 150) : (e.code == 429 ? Color.FromArgb(255, 190, 70) : Color.FromArgb(255, 120, 115));
                    Txt(g, hora, cell, Color.White, new RectangleF(c.X + ex[0], ey, 72, 18));
                    Txt(g, ModelQuotaGuard.ProviderLabel(e.bucket) + " · " + ShortName(e.model), cell, Color.White, new RectangleF(c.X + ex[1], ey, 295, 18));
                    Txt(g, res, cellB, rc, new RectangleF(c.X + ex[2], ey, 88, 18));
                    Txt(g, e.latency_ms > 0 ? e.latency_ms.ToString("0") + " ms" : "—", cell, Color.White, new RectangleF(c.X + ex[3], ey, 78, 18));
                    Txt(g, e.tok_in + e.tok_out > 0 ? Num(e.tok_in + e.tok_out) : "—", cell, Color.White, new RectangleF(c.X + ex[4], ey, 88, 18));
                    Txt(g, e.note, small, dim, new RectangleF(c.X + ex[5], ey + 2, c.Right - c.X - ex[5] - 16, 16));
                    ey += 18;
                }
            }
        }
    }
}
