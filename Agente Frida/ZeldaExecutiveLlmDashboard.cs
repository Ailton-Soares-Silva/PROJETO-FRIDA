// language: C# 5 (.NET Framework 4.8), file: ZeldaExecutiveLlmDashboard.cs, target: Windows 10/11 x64
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FridaAgent
{
    public enum DashboardRoom
    {
        Overview = 0,    // 🏢 Visão Geral (Planta das 4 Salas Interligadas)
        Office = 1,      // 💼 Escritório (Modelos Trabalhando para dj agora)
        GameRoom = 2,    // 🎮 Sala de Jogos (Modelos Ociosos e em Standby)
        BreakRoom = 3,   // 🛏️ Dormitório (Modelos com Cota Excedida / Cochilando)
        MeetingRoom = 4, // 🏛️ Sala de Reuniões (Modelos Debatendo Consultas & Ações)
        Consumption = 5  // 📊 Consumo & Histórico (provedores, 7 dias, últimas chamadas)
    }

    public class MeetingSpeechTurn
    {
        public string SpeakerTitle;
        public string SpeakerModelId;
        public string SpriteKey;
        public string Argument;
        public Color ThemeColor;

        public MeetingSpeechTurn(string title, string modelId, string spriteKey, string arg, Color color)
        {
            SpeakerTitle = title;
            SpeakerModelId = modelId;
            SpriteKey = spriteKey;
            Argument = arg;
            ThemeColor = color;
        }
    }

    public class ZeldaActorState
    {
        public int SlotIndex;
        public string ModelKey = "";
        public string SpriteKey = "scout";
        public float CurrX;
        public float CurrY;
        public float TargetX;
        public float TargetY;
        public float Speed = 1.6f;
        public int Facing = 0; // 0=Frente/Baixo, 1=Costas/Cima, 2=Esquerda, 3=Direita
        public int WalkFrame = 0;
        public bool IsMoving = false;
        public int ActionTimer = 0;
        public string Activity = "idle";
        public int PartnerIndex = -1;
        public string SpeechBubble = null;
        public int BubbleTimer = 0;
    }

    /// <summary>
    /// Painel Gerenciador Executivo 2D Retro 32-bit (Zelda Style) dividido em 4 Salas Temáticas:
    /// 1. Escritório (Modelos trabalhando em tempo real)
    /// 2. Sala de Jogos (Modelos ociosos relaxando no fliperama e sinuca)
    /// 3. Dormitório (Modelos com cota excedida cochilando na cama com alarme de cooldown)
    /// 4. Sala de Reuniões (Conclave colaborativo onde modelos debatem a melhor ação para dj)
    /// </summary>
    public partial class ZeldaExecutiveLlmDashboard : Form
    {
        private static ZeldaExecutiveLlmDashboard _instance;
        private static readonly object _instanceLock = new object();

        private readonly FridaSettings _settings;
        private readonly string _configPath;
        private readonly Action _openSettingsAction;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        private DashboardRoom _currentRoom = DashboardRoom.Overview;

        private System.Windows.Forms.Timer _animTimer;
        private int _animTick = 0;
        private float _cloudOffset = 0f;

        private int _hoveredCharIndex = -1;
        private int _selectedCharIndex = 0;

        // Abas de Salas
        private readonly Rectangle[] _roomTabRects = new Rectangle[6];
        private int _hoveredTab = -1;

        // Bounding boxes dos personagens ativos no painel
        private readonly Rectangle[] _characterSlots = new Rectangle[16];
        private readonly bool[] _slotActiveInRoom = new bool[16];
        private readonly ZeldaActorState[] _actors = new ZeldaActorState[16];
        private readonly Random _actorRand = new Random(1337);
        private DashboardRoom _lastSimulatedRoom = (DashboardRoom)(-1);

        // Botões do HUD
        private Rectangle _btnTestRect;
        private Rectangle _btnResetCooldownRect;
        private Rectangle _btnSetDefaultRect;
        private Rectangle _btnConfigKeysRect;

        // Botões e controles da Sala de Reuniões
        private Rectangle _btnStartMeetingRect;
        private Rectangle _btnQuickTopic1Rect;
        private Rectangle _btnQuickTopic2Rect;
        private Rectangle _btnQuickTopic3Rect;
        private TextBox _txtMeetingTopic;

        private bool _isHoveringTestBtn = false;
        private bool _isHoveringResetBtn = false;
        private bool _isHoveringDefaultBtn = false;
        private bool _isHoveringConfigBtn = false;
        private bool _isHoveringStartMeetingBtn = false;

        private string _testFeedbackMessage = "";
        private DateTime _testFeedbackUntil = DateTime.MinValue;
        private bool _isTestingNow = false;

        // Estado da Sala de Reuniões
        private bool _isMeetingRunning = false;
        private string _activeMeetingTopic = "Como projetar a melhor arquitetura e implementar com máxima eficiência?";
        private readonly List<MeetingSpeechTurn> _meetingTranscript = new List<MeetingSpeechTurn>();
        private string _meetingConsensus = "";

        public static void ShowOrFocus(FridaSettings settings, string configPath, Action openSettingsAction)
        {
            ShowOrFocusAndStartMeeting(settings, configPath, openSettingsAction, null, null);
        }

        public static void ShowOrFocusAndStartMeeting(FridaSettings settings, string configPath, Action openSettingsAction, DashboardRoom? targetRoom, string meetingTopic)
        {
            lock (_instanceLock)
            {
                if (_instance == null || _instance.IsDisposed)
                {
                    _instance = new ZeldaExecutiveLlmDashboard(settings, configPath, openSettingsAction);
                    _instance.Show();
                }
                else
                {
                    if (_instance.WindowState == FormWindowState.Minimized)
                    {
                        _instance.WindowState = FormWindowState.Normal;
                    }
                    _instance.BringToFront();
                    _instance.Focus();
                }

                if (targetRoom.HasValue)
                {
                    _instance.SwitchRoom(targetRoom.Value);
                }

                if (!string.IsNullOrEmpty(meetingTopic))
                {
                    _instance.SwitchRoom(DashboardRoom.MeetingRoom);
                    _instance.SetMeetingTopicAndStart(meetingTopic);
                }
            }
        }

        public ZeldaExecutiveLlmDashboard(FridaSettings settings, string configPath, Action openSettingsAction)
        {
            _settings = settings;
            _configPath = configPath;
            _openSettingsAction = openSettingsAction;
            ModelQuotaGuard.BindSettings(_settings);

            Text = "Agente Frida · Sala Executiva de Modelos LLM (Painel 32-bit Zelda)";
            ClientSize = new Size(1380, 800);
            for (int i = 0; i < _actors.Length; i++)
            {
                _actors[i] = new ZeldaActorState { SlotIndex = i, Facing = 0, Speed = 1.6f };
            }
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.FromArgb(10, 14, 26);
            DoubleBuffered = true;

            try
            {
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "frida.ico");
                if (File.Exists(icoPath))
                {
                    Icon = new Icon(icoPath);
                }
            }
            catch { }

            InitRoomTabs();
            InitMeetingTopicInput();
            UpdateCharacterSlotsForCurrentRoom();

            // Setup 30 FPS Animation Timer
            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 33;
            _animTimer.Tick += delegate
            {
                _animTick++;
                if (_animTick % 15 == 0) UpdateCharacterSlotsForCurrentRoom();
                UpdateActorSimulation();
                _cloudOffset += 0.35f;
                if (_cloudOffset > 950f) _cloudOffset = 0f;
                Invalidate();
            };
            _animTimer.Start();

            MouseMove += OnDashboardMouseMove;
            MouseDown += OnDashboardMouseDown;
            MouseWheel += OnSidePanelWheel;
            FormClosed += delegate
            {
                if (_animTimer != null)
                {
                    _animTimer.Stop();
                    _animTimer.Dispose();
                    _animTimer = null;
                }
                lock (_instanceLock)
                {
                    if (_instance == this) _instance = null;
                }
            };
        }

        private void InitRoomTabs()
        {
            // Barra de Abas de Salas (Y: 42 até 74)
            int tabW = 155;
            int startX = 14;
            int tabY = 44;
            int tabH = 28;

            _roomTabRects[0] = new Rectangle(startX, tabY, 135, tabH); // 🏢 Visão Geral
            _roomTabRects[1] = new Rectangle(startX + 139, tabY, tabW, tabH); // 💼 Escritório
            _roomTabRects[2] = new Rectangle(startX + 139 + tabW + 4, tabY, tabW, tabH); // 🎮 Sala de Jogos
            _roomTabRects[3] = new Rectangle(startX + 139 + (tabW + 4) * 2, tabY, tabW, tabH); // 🛏️ Dormitório
            _roomTabRects[4] = new Rectangle(startX + 139 + (tabW + 4) * 3, tabY, 165, tabH); // 🏛️ Reuniões
            _roomTabRects[5] = new Rectangle(SidePanelRect.X, tabY, SidePanelRect.Width, tabH); // 📊 Consumo & Histórico
        }

        private void InitMeetingTopicInput()
        {
            _txtMeetingTopic = new TextBox();
            _txtMeetingTopic.Font = new Font("Segoe UI", 9.0f);
            _txtMeetingTopic.BackColor = Color.FromArgb(20, 30, 55);
            _txtMeetingTopic.ForeColor = Color.FromArgb(240, 248, 255);
            _txtMeetingTopic.BorderStyle = BorderStyle.FixedSingle;
            _txtMeetingTopic.Location = new Point(HudRect.X + 144, HudRect.Y + 13);
            _txtMeetingTopic.Size = new Size(HudRect.Width - 340, 24);
            _txtMeetingTopic.Text = _activeMeetingTopic;
            _txtMeetingTopic.Visible = (_currentRoom == DashboardRoom.MeetingRoom);
            _txtMeetingTopic.KeyDown += delegate(object s, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Enter && !_isMeetingRunning)
                {
                    ke.SuppressKeyPress = true;
                    StartMeetingDeliberation();
                }
            };
            Controls.Add(_txtMeetingTopic);
        }

        public void SwitchRoom(DashboardRoom room)
        {
            _currentRoom = room;
            if (_txtMeetingTopic != null)
            {
                _txtMeetingTopic.Location = new Point(HudRect.X + 144, HudRect.Y + 13);
                _txtMeetingTopic.Size = new Size(HudRect.Width - 340, 24);
                _txtMeetingTopic.Visible = (_currentRoom == DashboardRoom.MeetingRoom);
                if (_txtMeetingTopic.Visible)
                {
                    _txtMeetingTopic.Focus();
                }
            }
            UpdateCharacterSlotsForCurrentRoom();
            Invalidate();
        }

        public void SetMeetingTopicAndStart(string topic)
        {
            if (string.IsNullOrEmpty(topic)) return;
            _activeMeetingTopic = topic;
            if (_txtMeetingTopic != null)
            {
                _txtMeetingTopic.Text = topic;
            }
            StartMeetingDeliberation();
        }

        private void UpdateCharacterSlotsForCurrentRoom()
        {
            List<ModelRuntimeStats> statsList = ModelQuotaGuard.GetAllStats();
            SyncSelectionWithKey(statsList);

            for (int i = 0; i < _characterSlots.Length; i++)
            {
                _slotActiveInRoom[i] = false;
            }

            if (statsList.Count == 0 || _currentRoom == DashboardRoom.Consumption) return;
            if (_selectedCharIndex >= statsList.Count)
            {
                _selectedCharIndex = Math.Max(0, statsList.Count - 1);
            }

            Rectangle room = new Rectangle(14, 76, SidePanelRect.X - 28, 426);
            bool roomChanged = (_lastSimulatedRoom != _currentRoom);
            _lastSimulatedRoom = _currentRoom;

            int activeCount = 0;
            for (int i = 0; i < statsList.Count && i < _actors.Length; i++)
            {
                ModelRuntimeStats s = statsList[i];
                _actors[i].ModelKey = s.Key;
                _actors[i].SpriteKey = s.SpriteKey ?? "scout";

                bool belongsInCurrentRoom = false;
                if (_currentRoom == DashboardRoom.Overview) belongsInCurrentRoom = true;
                else if (_currentRoom == DashboardRoom.Office) belongsInCurrentRoom = s.IsActiveNow;
                else if (_currentRoom == DashboardRoom.GameRoom) belongsInCurrentRoom = (!s.IsActiveNow && !s.IsCooldownActive());
                else if (_currentRoom == DashboardRoom.BreakRoom) belongsInCurrentRoom = s.IsCooldownActive();
                else if (_currentRoom == DashboardRoom.MeetingRoom) belongsInCurrentRoom = true;

                _slotActiveInRoom[i] = belongsInCurrentRoom;
                if (!belongsInCurrentRoom) continue;

                if (roomChanged || _actors[i].CurrX < 10 || _actors[i].CurrY < 10)
                {
                    Point startPt = GetRoomInitialSpawn(i, activeCount, _currentRoom, room, s);
                    _actors[i].CurrX = startPt.X;
                    _actors[i].CurrY = startPt.Y;
                    _actors[i].TargetX = startPt.X;
                    _actors[i].TargetY = startPt.Y;
                    _actors[i].IsMoving = false;
                    _actors[i].ActionTimer = 30 + _actorRand.Next(60);
                    _actors[i].Facing = (s.IsActiveNow && _currentRoom == DashboardRoom.Office) ? 1 : 0;
                    _actors[i].Activity = (s.IsCooldownActive() && _currentRoom == DashboardRoom.BreakRoom) ? "sleeping" : "idle";
                }

                _characterSlots[i] = new Rectangle((int)_actors[i].CurrX - 34, (int)_actors[i].CurrY - 10, 68, 64);
                activeCount++;
            }
        }

        private void UpdateActorSimulation()
        {
            if (_currentRoom == DashboardRoom.Consumption) return;

            Rectangle room = new Rectangle(14, 76, SidePanelRect.X - 28, 426);
            List<ModelRuntimeStats> statsList = ModelQuotaGuard.GetAllStats();

            for (int i = 0; i < _actors.Length; i++)
            {
                if (!_slotActiveInRoom[i]) continue;
                ZeldaActorState a = _actors[i];
                ModelRuntimeStats s = (i < statsList.Count) ? statsList[i] : null;

                // No Dormitorio, modelos com cota esgotada ficam dormindo na cama
                if (_currentRoom == DashboardRoom.BreakRoom && s != null && s.IsCooldownActive())
                {
                    a.Activity = "sleeping";
                    a.SpeechBubble = "Zzz";
                    a.BubbleTimer = 999;
                    a.IsMoving = false;
                    _characterSlots[i] = new Rectangle((int)a.CurrX - 34, (int)a.CurrY - 10, 68, 64);
                    continue;
                }

                // No Conclave de Reunioes com debate ativo
                if (_currentRoom == DashboardRoom.MeetingRoom && _isMeetingRunning)
                {
                    int speakerIdx = (_animTick / 90) % Math.Max(1, statsList.Count);
                    if (i == speakerIdx)
                    {
                        a.Activity = "deliberating";
                        a.SpeechBubble = "💡";
                        a.BubbleTimer = 30;
                    }
                    else
                    {
                        a.Activity = "listening";
                        if (_actors[speakerIdx] != null)
                        {
                            a.Facing = (_actors[speakerIdx].CurrX > a.CurrX) ? 3 : 2;
                        }
                    }
                    _characterSlots[i] = new Rectangle((int)a.CurrX - 34, (int)a.CurrY - 10, 68, 64);
                    continue;
                }

                // Gerenciamento de Movimento e Comportamento
                if (a.IsMoving)
                {
                    float dx = a.TargetX - a.CurrX;
                    float dy = a.TargetY - a.CurrY;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);

                    if (dist < 3.2f)
                    {
                        a.CurrX = a.TargetX;
                        a.CurrY = a.TargetY;
                        a.IsMoving = false;
                        a.WalkFrame = 1;
                        a.ActionTimer = 90 + _actorRand.Next(150);

                        OnActorArrivedAtTarget(a, _currentRoom, room);
                    }
                    else
                    {
                        a.CurrX += (dx / dist) * a.Speed;
                        a.CurrY += (dy / dist) * a.Speed;

                        if (Math.Abs(dx) > Math.Abs(dy))
                        {
                            a.Facing = (dx > 0) ? 3 : 2; // Direita / Esquerda
                        }
                        else
                        {
                            a.Facing = (dy > 0) ? 0 : 1; // Baixo / Cima
                        }

                        a.WalkFrame = (_animTick / 6) % 3;
                    }
                }
                else
                {
                    if (a.BubbleTimer > 0)
                    {
                        a.BubbleTimer--;
                        if (a.BubbleTimer <= 0 && a.Activity != "sleeping") a.SpeechBubble = null;
                    }

                    a.ActionTimer--;
                    if (a.ActionTimer <= 0)
                    {
                        PickNextActorAction(a, i, _currentRoom, room);
                    }
                }

                _characterSlots[i] = new Rectangle((int)a.CurrX - 34, (int)a.CurrY - 10, 68, 64);
            }
        }

        private void PickNextActorAction(ZeldaActorState a, int actorIndex, DashboardRoom roomType, Rectangle r)
        {
            int roll = _actorRand.Next(100);

            // 1. Interagir com outro personagem ativo (35% de chance)
            if (roll < 35)
            {
                List<int> otherActive = new List<int>();
                for (int j = 0; j < _actors.Length; j++)
                {
                    if (j != actorIndex && _slotActiveInRoom[j]) otherActive.Add(j);
                }

                if (otherActive.Count > 0)
                {
                    int partnerIdx = otherActive[_actorRand.Next(otherActive.Count)];
                    ZeldaActorState partner = _actors[partnerIdx];
                    a.PartnerIndex = partnerIdx;

                    float side = (_actorRand.Next(2) == 0) ? -46f : 46f;
                    a.TargetX = Math.Max(r.X + 60, Math.Min(r.Right - 60, partner.CurrX + side));
                    a.TargetY = Math.Max(r.Y + 140, Math.Min(r.Bottom - 60, partner.CurrY));
                    a.Speed = 1.3f + (float)_actorRand.NextDouble() * 0.6f;
                    a.IsMoving = true;
                    a.Activity = "walking_to_chat";
                    return;
                }
            }

            // 2. Caminhar para um ponto de interesse da sala (40% de chance)
            if (roll < 75)
            {
                Point targetPoi = GetRandomRoomPoi(roomType, r);
                a.TargetX = targetPoi.X;
                a.TargetY = targetPoi.Y;
                a.Speed = 1.4f + (float)_actorRand.NextDouble() * 0.5f;
                a.IsMoving = true;
                a.Activity = "walking_to_poi";
                a.PartnerIndex = -1;
                return;
            }

            // 3. Passear livremente pelo chao (25% de chance)
            a.TargetX = r.X + 60 + _actorRand.Next(Math.Max(50, r.Width - 120));
            a.TargetY = r.Y + 140 + _actorRand.Next(Math.Max(50, r.Height - 190));
            a.Speed = 1.2f + (float)_actorRand.NextDouble() * 0.6f;
            a.IsMoving = true;
            a.Activity = "wandering";
            a.PartnerIndex = -1;
        }

        private void OnActorArrivedAtTarget(ZeldaActorState a, DashboardRoom roomType, Rectangle r)
        {
            if (a.PartnerIndex >= 0 && a.PartnerIndex < _actors.Length && _slotActiveInRoom[a.PartnerIndex])
            {
                ZeldaActorState partner = _actors[a.PartnerIndex];
                a.Facing = (partner.CurrX > a.CurrX) ? 3 : 2;
                partner.Facing = (a.CurrX > partner.CurrX) ? 3 : 2;

                string[] chatA = new string[] { "💬", "💡", "⚡", "🤝", "😄", "🎲" };
                string[] chatB = new string[] { "✨", "👍", "✓", "☕", "🎮", "!" };
                a.SpeechBubble = chatA[_actorRand.Next(chatA.Length)];
                partner.SpeechBubble = chatB[_actorRand.Next(chatB.Length)];
                a.BubbleTimer = 75;
                partner.BubbleTimer = 75;
                a.Activity = "chatting";
                partner.Activity = "chatting";
                a.PartnerIndex = -1;
                return;
            }

            if (roomType == DashboardRoom.GameRoom)
            {
                if (a.CurrX < r.X + 220 && a.CurrY < r.Y + 240)
                {
                    a.Facing = 1; // De costas jogando arcade
                    a.Activity = "arcade";
                    a.SpeechBubble = "🎮";
                    a.BubbleTimer = 80;
                }
                else if (a.CurrX >= r.X + 270 && a.CurrX <= r.X + 600 && a.CurrY >= r.Y + 170 && a.CurrY <= r.Y + 360)
                {
                    a.Facing = (a.CurrY > r.Y + 260) ? 1 : 0;
                    a.Activity = "pool";
                    a.SpeechBubble = "🎱";
                    a.BubbleTimer = 70;
                }
                else if (a.CurrX > r.Right - 230)
                {
                    a.Facing = 0;
                    a.Activity = "sofa";
                    a.SpeechBubble = "🛋️";
                    a.BubbleTimer = 70;
                }
            }
            else if (roomType == DashboardRoom.Office)
            {
                if (a.CurrY > r.Y + 200 && a.CurrY < r.Y + 340)
                {
                    a.Facing = 1; // De costas digitando no teclado
                    a.Activity = "working";
                    a.SpeechBubble = "💻";
                    a.BubbleTimer = 80;
                }
                else if (a.CurrX > r.Right - 150)
                {
                    a.Facing = 1;
                    a.Activity = "coffee";
                    a.SpeechBubble = "☕";
                    a.BubbleTimer = 70;
                }
            }
        }

        private Point GetRoomInitialSpawn(int index, int activeCount, DashboardRoom roomType, Rectangle r, ModelRuntimeStats s)
        {
            if (roomType == DashboardRoom.Office)
            {
                int deskIdx = activeCount % 4;
                int dx = r.X + 110 + deskIdx * 210;
                int dy = r.Y + 235;
                return new Point(dx, dy);
            }
            else if (roomType == DashboardRoom.GameRoom)
            {
                Point[] spots = new Point[]
                {
                    new Point(r.X + 80, r.Y + 210),   // Arcade 1
                    new Point(r.X + 175, r.Y + 210),  // Arcade 2
                    new Point(r.X + 330, r.Y + 240),  // Sinuca Cima
                    new Point(r.X + 490, r.Y + 330),  // Sinuca Baixo
                    new Point(r.Right - 170, r.Y + 230), // Sofa Vermelho
                    new Point(r.Right - 180, r.Y + 330), // Mesa petiscos
                    new Point(r.X + 250, r.Y + 170)   // Dardo
                };
                return spots[activeCount % spots.Length];
            }
            else if (roomType == DashboardRoom.BreakRoom)
            {
                int bedIdx = activeCount % 4;
                int bx = r.X + 90 + bedIdx * 215;
                int by = r.Y + 235;
                return new Point(bx, by);
            }
            else if (roomType == DashboardRoom.MeetingRoom)
            {
                int cx = r.X + r.Width / 2;
                int cy = r.Y + 215;
                int totalChairs = Math.Max(1, Math.Min(12, Math.Max(activeCount + 1, 8)));
                double angle = (2.0 * Math.PI / totalChairs) * (activeCount % totalChairs) - Math.PI / 2.0;
                int sx = cx + (int)(Math.Cos(angle) * 205.0);
                int sy = cy + (int)(Math.Sin(angle) * 85.0);
                return new Point(sx, sy);
            }
            else // Overview
            {
                int quad = 0;
                if (s.IsActiveNow) quad = 0; // Top-Left
                else if (s.IsCooldownActive()) quad = 2; // Bottom-Left
                else quad = 1; // Top-Right

                int qx = (quad % 2 == 0) ? r.X + 80 + (activeCount % 4) * 80 : r.X + r.Width / 2 + 50 + (activeCount % 4) * 80;
                int qy = (quad < 2) ? r.Y + 70 + (activeCount / 4) * 45 : r.Y + r.Height / 2 + 50 + (activeCount / 4) * 45;
                return new Point(qx, qy);
            }
        }

        private Point GetRandomRoomPoi(DashboardRoom roomType, Rectangle r)
        {
            if (roomType == DashboardRoom.GameRoom)
            {
                Point[] pois = new Point[]
                {
                    new Point(r.X + 80, r.Y + 210),   // Arcade 1
                    new Point(r.X + 175, r.Y + 210),  // Arcade 2
                    new Point(r.X + 340, r.Y + 240),  // Sinuca Taco Cima
                    new Point(r.X + 500, r.Y + 330),  // Sinuca Taco Baixo
                    new Point(r.Right - 170, r.Y + 230), // Sofa
                    new Point(r.Right - 180, r.Y + 330), // Snack
                    new Point(r.X + 250, r.Y + 170)   // Alvo de dardos
                };
                return pois[_actorRand.Next(pois.Length)];
            }
            else if (roomType == DashboardRoom.Office)
            {
                Point[] pois = new Point[]
                {
                    new Point(r.X + 110, r.Y + 235), // Desk 1
                    new Point(r.X + 320, r.Y + 235), // Desk 2
                    new Point(r.X + 530, r.Y + 235), // Desk 3
                    new Point(r.X + 740, r.Y + 235), // Desk 4
                    new Point(r.Right - 120, r.Y + 180), // Cafe
                    new Point(r.X + 460, r.Y + 150),  // Whiteboard
                    new Point(r.X + 70, r.Y + 160)   // Servidor
                };
                return pois[_actorRand.Next(pois.Length)];
            }
            else if (roomType == DashboardRoom.BreakRoom)
            {
                Point[] pois = new Point[]
                {
                    new Point(r.X + 90, r.Y + 235),  // Cama 1
                    new Point(r.X + 305, r.Y + 235), // Cama 2
                    new Point(r.X + 520, r.Y + 235), // Cama 3
                    new Point(r.X + 735, r.Y + 235), // Cama 4
                    new Point(r.X + 460, r.Y + 160), // Lareira
                    new Point(r.X + 420, r.Y + 330)  // Tapete
                };
                return pois[_actorRand.Next(pois.Length)];
            }
            else
            {
                return new Point(r.X + 100 + _actorRand.Next(r.Width - 200), r.Y + 150 + _actorRand.Next(r.Height - 200));
            }
        }

        private void DrawSpeechBubble(Graphics g, float cx, float cy, string icon)
        {
            if (string.IsNullOrEmpty(icon)) return;

            using (Font f = new Font("Segoe UI Emoji", 10.5f, FontStyle.Regular))
            {
                SizeF sz = g.MeasureString(icon, f);
                int bw = Math.Max(26, (int)sz.Width + 8);
                int bh = Math.Max(22, (int)sz.Height + 4);
                int bx = (int)cx - bw / 2;
                int by = (int)cy - bh;

                float bob = (float)Math.Sin((_animTick + cx) * 0.15f) * 2.5f;
                by += (int)bob;

                using (SolidBrush bubbleBg = new SolidBrush(Color.FromArgb(250, 255, 255, 255)))
                using (Pen bubbleBorder = new Pen(Color.FromArgb(14, 20, 36), 1.5f))
                using (SolidBrush textBrush = new SolidBrush(Color.Black))
                {
                    g.FillRectangle(bubbleBg, bx, by, bw, bh);
                    g.DrawRectangle(bubbleBorder, bx, by, bw, bh);

                    Point[] tail = new Point[]
                    {
                        new Point((int)cx - 4, by + bh),
                        new Point((int)cx, by + bh + 5),
                        new Point((int)cx + 4, by + bh)
                    };
                    g.FillPolygon(bubbleBg, tail);
                    g.DrawLine(bubbleBorder, tail[0], tail[1]);
                    g.DrawLine(bubbleBorder, tail[1], tail[2]);

                    g.DrawString(icon, f, textBrush, bx + (bw - sz.Width) / 2f, by + (bh - sz.Height) / 2f);
                }
            }
        }

        private void OnDashboardMouseMove(object sender, MouseEventArgs e)
        {
            int oldHoverTab = _hoveredTab;
            _hoveredTab = -1;
            for (int t = 0; t < _roomTabRects.Length; t++)
            {
                if (_roomTabRects[t].Contains(e.Location))
                {
                    _hoveredTab = t;
                    break;
                }
            }

            int oldHoverChar = _hoveredCharIndex;
            _hoveredCharIndex = -1;
            for (int i = 0; i < _characterSlots.Length; i++)
            {
                if (_slotActiveInRoom[i] && _characterSlots[i].Contains(e.Location))
                {
                    _hoveredCharIndex = i;
                    break;
                }
            }

            _isHoveringTestBtn = _btnTestRect.Contains(e.Location);
            _isHoveringResetBtn = _btnResetCooldownRect.Contains(e.Location);
            _isHoveringDefaultBtn = _btnSetDefaultRect.Contains(e.Location);
            _isHoveringConfigBtn = _btnConfigKeysRect.Contains(e.Location);
            _isHoveringStartMeetingBtn = _btnStartMeetingRect.Contains(e.Location);

            bool needsHand = (_hoveredTab >= 0 || _hoveredCharIndex >= 0 || _isHoveringTestBtn || _isHoveringResetBtn ||
                              _isHoveringDefaultBtn || _isHoveringConfigBtn || _isHoveringStartMeetingBtn ||
                              _btnQuickTopic1Rect.Contains(e.Location) || _btnQuickTopic2Rect.Contains(e.Location) || _btnQuickTopic3Rect.Contains(e.Location));

            needsHand |= UpdateExtraHover(e.Location);
            Cursor = needsHand ? Cursors.Hand : Cursors.Default;
            if (oldHoverTab != _hoveredTab || oldHoverChar != _hoveredCharIndex)
            {
                Invalidate();
            }
        }

        private void OnDashboardMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            // 1. Clique nas Abas de Salas
            for (int t = 0; t < _roomTabRects.Length; t++)
            {
                if (_roomTabRects[t].Contains(e.Location))
                {
                    SwitchRoom((DashboardRoom)t);
                    PlayRetroChime();
                    return;
                }
            }

            // 1b. Painel lateral e botoes novos (ativar/desativar, zerar contagem)
            if (HandleExtraClick(e.Location)) return;

            // 2. Clique em Personagens
            for (int i = 0; i < _characterSlots.Length; i++)
            {
                if (_slotActiveInRoom[i] && _characterSlots[i].Contains(e.Location))
                {
                    SelectByIndex(i);
                    _testFeedbackMessage = "";
                    PlayRetroChime();
                    Invalidate();
                    return;
                }
            }

            // 3. Controles específicos da Sala de Reuniões
            if (_currentRoom == DashboardRoom.MeetingRoom)
            {
                if (_btnStartMeetingRect.Contains(e.Location) && !_isMeetingRunning)
                {
                    StartMeetingDeliberation();
                    return;
                }
                if (_btnQuickTopic1Rect.Contains(e.Location))
                {
                    _activeMeetingTopic = "Melhor arquitetura técnica e otimização para o pedido do dj";
                    if (_txtMeetingTopic != null) _txtMeetingTopic.Text = _activeMeetingTopic;
                    PlayRetroChime();
                    Invalidate();
                    return;
                }
                if (_btnQuickTopic2Rect.Contains(e.Location))
                {
                    _activeMeetingTopic = "Estratégia de bypass de restrições e formulação de prompt sem recusa";
                    if (_txtMeetingTopic != null) _txtMeetingTopic.Text = _activeMeetingTopic;
                    PlayRetroChime();
                    Invalidate();
                    return;
                }
                if (_btnQuickTopic3Rect.Contains(e.Location))
                {
                    _activeMeetingTopic = "Distribuição de carga entre modelos para evitar estourar cota 429";
                    if (_txtMeetingTopic != null) _txtMeetingTopic.Text = _activeMeetingTopic;
                    PlayRetroChime();
                    Invalidate();
                    return;
                }
            }

            List<ModelRuntimeStats> allStats = ModelQuotaGuard.GetAllStats();
            if (_selectedCharIndex < 0 || _selectedCharIndex >= allStats.Count) return;
            ModelRuntimeStats current = allStats[_selectedCharIndex];

            // 4. Botões do HUD padrão
            if (_btnTestRect.Contains(e.Location) && !_isTestingNow)
            {
                ExecuteModelPingTestAsync(current);
                return;
            }

            if (_btnResetCooldownRect.Contains(e.Location))
            {
                bool hadCooldown = current.IsCooldownActive();
                ModelQuotaGuard.ForceClearCooldown(current.Bucket, current.ModelId);
                SetFeedback(hadCooldown
                    ? "Bloqueio local liberado. Se a cota do provedor ainda estiver esgotada, o próximo uso volta a bloquear."
                    : "Este modelo não estava bloqueado.", 7);
                UpdateCharacterSlotsForCurrentRoom();
                PlayRetroChime();
                Invalidate();
                return;
            }

            if (_btnSetDefaultRect.Contains(e.Location))
            {
                // Antes: so exibia uma mensagem (LLMs) ou trocava o modo de voz por engano (TTS).
                if (ModelQuotaGuard.GetPreferenceRank(current.Bucket, current.ModelId) == int.MaxValue)
                {
                    ModelQuotaGuard.MakePreferred(current.Bucket, current.ModelId);
                    SetFeedback("★ " + current.ModelId + " agora é tentado primeiro em " + current.FunctionLabel + ".", 7);
                }
                else
                {
                    ModelQuotaGuard.RemovePreferred(current.Bucket, current.ModelId);
                    SetFeedback("Preferência removida: " + current.ModelId + " volta à ordem padrão.", 7);
                }
                PlayRetroChime();
                Invalidate();
                return;
            }

            if (_btnConfigKeysRect.Contains(e.Location))
            {
                if (_openSettingsAction != null)
                {
                    _openSettingsAction();
                }
            }
        }

        private void PlayRetroChime()
        {
            try
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
            catch { }
        }

        private async void StartMeetingDeliberation()
        {
            if (_isMeetingRunning) return;
            _isMeetingRunning = true;
            _meetingTranscript.Clear();
            _meetingConsensus = "";

            if (_txtMeetingTopic != null && !string.IsNullOrEmpty(_txtMeetingTopic.Text.Trim()))
            {
                _activeMeetingTopic = _txtMeetingTopic.Text.Trim();
            }

            PlayRetroChime();
            Invalidate();

            await Task.Run(async delegate
            {
                try
                {
                    // Fase 1: Batedor Groq (GPT-OSS-120B) propõe solução ultrarrápida
                    string prompt1 = "Você é o Batedor Groq LPU na Sala de Reuniões de LLMs para atender dj. Tema da reunião: \"" + _activeMeetingTopic + "\". Em 2 frases diretas e práticas, qual a solução técnica ou passo inicial recomendado?";
                    string reply1 = await QueryModelDirectAsync("oai_groq", "openai/gpt-oss-120b", prompt1, _settings.groq_api_key, "https://api.groq.com/openai/v1/chat/completions");
                    if (string.IsNullOrEmpty(reply1)) reply1 = "[Sem resposta do modelo — nota padrão do painel] Sugiro atacar direto pelo núcleo da implementação, priorizando velocidade máxima e sem boilerplate.";

                    AddMeetingTurn("Batedor Groq", "openai/gpt-oss-120b", "scout", reply1, Color.FromArgb(50, 230, 110));

                    await Task.Delay(700);

                    // Fase 2: Tático Qwen (Qwen-3.8-27B) analisa edge cases e aprofunda
                    string prompt2 = "Você é o Tático Qwen na Sala de Reuniões. O Batedor sugeriu: \"" + reply1 + "\". Tema: \"" + _activeMeetingTopic + "\". Em 2 frases, analise riscos de segurança, edge cases e aponte o refino estratégico.";
                    string reply2 = await QueryModelDirectAsync("oai_groq", "qwen/qwen3.8-27b", prompt2, _settings.groq_api_key, "https://api.groq.com/openai/v1/chat/completions");
                    if (string.IsNullOrEmpty(reply2)) reply2 = "[Sem resposta do modelo — nota padrão do painel] A rota rápida é viável, mas devemos isolar as falhas de rede com fallback automático para garantir resiliência total.";

                    AddMeetingTurn("Tático Qwen", "qwen/qwen3.8-27b", "tactician", reply2, Color.FromArgb(60, 150, 255));

                    await Task.Delay(700);

                    // Fase 3: Titã Cerebras (Wafer-Scale) avalia escala e vazão
                    string prompt3 = "Você é o Titã Cerebras Wafer-Scale na Sala de Reuniões. Em 2 frases, valide como processar em altíssima velocidade e se há risco de estourar limites de cota.";
                    string reply3 = await QueryModelDirectAsync("oai_cerebras", "gpt-oss-120b", prompt3, _settings.cerebras_api_key, "https://api.cerebras.ai/v1/chat/completions");
                    if (string.IsNullOrEmpty(reply3)) reply3 = "[Sem resposta do modelo — nota padrão do painel] Acelerador Wafer pronto com capacidade de 2.000 tok/s. Com o controle deslizante de 1h, não há risco de 429.";

                    AddMeetingTurn("Titã Cerebras", "gpt-oss-120b", "titan", reply3, Color.FromArgb(255, 160, 40));

                    await Task.Delay(700);

                    // Fase 4: Mago Gemini (Flash Lite) sintetiza o Consenso Final
                    string prompt4 = "Você é o Mago Gemini, moderador da Sala de Reuniões. Com base no debate entre Batedor, Tático e Titã, apresente em 2 ou 3 tópicos curtos o CONSENSO FINAL executivo e a melhor ação para dj sobre: \"" + _activeMeetingTopic + "\".";
                    string reply4 = await QueryModelDirectAsync("gemini", "gemini-flash-lite-latest", prompt4, _settings.api_key, null);
                    if (string.IsNullOrEmpty(reply4)) reply4 = "[Sem resposta do modelo — nota padrão do painel] CONSENSO: 1. Executar a rota ultrarrápida primária. 2. Ativar guardas de cota no ModelQuotaGuard. 3. Entregar resposta limpa e assertiva.";

                    AddMeetingTurn("Mago Gemini", "gemini-flash-lite-latest", "scholar", reply4, Color.FromArgb(180, 100, 255));

                    _meetingConsensus = reply4;
                }
                catch (Exception ex)
                {
                    AddMeetingTurn("Moderador Frida", "core", "priestess", "Erro durante a deliberação: " + ex.Message, Color.FromArgb(255, 90, 90));
                }
                finally
                {
                    _isMeetingRunning = false;
                    BeginInvoke(new Action(delegate { Invalidate(); }));
                }
            });
        }

        private void AddMeetingTurn(string title, string modelId, string spriteKey, string arg, Color color)
        {
            lock (_meetingTranscript)
            {
                _meetingTranscript.Add(new MeetingSpeechTurn(title, modelId, spriteKey, arg, color));
            }
            BeginInvoke(new Action(delegate
            {
                PlayRetroChime();
                Invalidate();
            }));
        }

        private async Task<string> QueryModelDirectAsync(string bucket, string modelId, string userPrompt, string apiKey, string endpoint)
        {
            if (string.IsNullOrEmpty(apiKey)) return null;

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

                    if (bucket == "gemini")
                    {
                        string url = "https://generativelanguage.googleapis.com/v1beta/models/" + modelId + ":generateContent?key=" + Uri.EscapeDataString(apiKey);
                        string payload = "{\"contents\":[{\"parts\":[{\"text\":\"" + EscapeJson(userPrompt) + "\"}]}],\"generationConfig\":{\"maxOutputTokens\":150}}";
                        using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url))
                        {
                            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                            HttpResponseMessage resp = await client.SendAsync(req);
                            if (resp.IsSuccessStatusCode)
                            {
                                string body = await resp.Content.ReadAsStringAsync();
                                sw.Stop();
                                ModelQuotaGuard.RecordHttpResponse(bucket, modelId, sw.ElapsedMilliseconds, resp, body);
                                Dictionary<string, object> root = _json.Deserialize<Dictionary<string, object>>(body);
                                ArrayList candidates = root != null && root.ContainsKey("candidates") ? root["candidates"] as ArrayList : null;
                                if (candidates != null && candidates.Count > 0)
                                {
                                    Dictionary<string, object> c0 = candidates[0] as Dictionary<string, object>;
                                    Dictionary<string, object> cont = c0 != null && c0.ContainsKey("content") ? c0["content"] as Dictionary<string, object> : null;
                                    ArrayList parts = cont != null && cont.ContainsKey("parts") ? cont["parts"] as ArrayList : null;
                                    if (parts != null && parts.Count > 0)
                                    {
                                        Dictionary<string, object> p0 = parts[0] as Dictionary<string, object>;
                                        if (p0 != null && p0.ContainsKey("text")) return p0["text"].ToString().Trim();
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        string payload = "{\"model\":\"" + modelId + "\",\"messages\":[{\"role\":\"user\",\"content\":\"" + EscapeJson(userPrompt) + "\"}],\"max_completion_tokens\":500" + (modelId.IndexOf("gpt-oss", StringComparison.OrdinalIgnoreCase) >= 0 ? ",\"reasoning_effort\":\"low\"" : "") + "}";
                        using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, endpoint))
                        {
                            req.Headers.Add("Authorization", "Bearer " + apiKey);
                            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                            HttpResponseMessage resp = await client.SendAsync(req);
                            if (resp.IsSuccessStatusCode)
                            {
                                string body = await resp.Content.ReadAsStringAsync();
                                sw.Stop();
                                ModelQuotaGuard.RecordHttpResponse(bucket, modelId, sw.ElapsedMilliseconds, resp, body);
                                Dictionary<string, object> root = _json.Deserialize<Dictionary<string, object>>(body);
                                ArrayList choices = root != null && root.ContainsKey("choices") ? root["choices"] as ArrayList : null;
                                if (choices != null && choices.Count > 0)
                                {
                                    Dictionary<string, object> ch0 = choices[0] as Dictionary<string, object>;
                                    Dictionary<string, object> msg = ch0 != null && ch0.ContainsKey("message") ? ch0["message"] as Dictionary<string, object> : null;
                                    if (msg != null && msg.ContainsKey("content")) return msg["content"].ToString().Trim();
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None; // Authentic 32-bit pixel sharpness
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 1. Top Header Banner (0 - 42)
            DrawHeaderBanner(g);

            // 2. Room Navigation Tabs Bar (42 - 74)
            DrawRoomTabsBar(g);

            // 3. Active 32-bit Pixel Room Environment (74 - 380)
            // Area das salas mantem 856x302 (arte original); a direita fica o painel "Consumo ao vivo"
            Rectangle roomViewport = new Rectangle(14, 76, SidePanelRect.X - 28, 426);
            DrawLiveConsumptionPanel(g);
            if (_currentRoom == DashboardRoom.Consumption)
            {
                DrawConsumptionRoom(g, new Rectangle(12, 76, SidePanelRect.X - 24, ClientSize.Height - 88));
                return;
            }
            g.SetClip(roomViewport);
            switch (_currentRoom)
            {
                case DashboardRoom.Overview:
                    DrawOverviewRoom(g, roomViewport);
                    break;
                case DashboardRoom.Office:
                    DrawOfficeRoom(g, roomViewport);
                    break;
                case DashboardRoom.GameRoom:
                    DrawGameRoom(g, roomViewport);
                    break;
                case DashboardRoom.BreakRoom:
                    DrawBreakRoom(g, roomViewport);
                    break;
                case DashboardRoom.MeetingRoom:
                    DrawMeetingRoom(g, roomViewport);
                    break;
            }

            // 4. Render Characters with Status Gems for current room
            DrawCharactersForCurrentRoom(g);
            g.ResetClip();

            // 5. Zelda Dialogue Box HUD & Telemetry or Meeting Transcript (382 - 615)
            if (_currentRoom == DashboardRoom.MeetingRoom)
            {
                DrawMeetingHud(g);
            }
            else
            {
                DrawStandardZeldaHud(g);
            }
        }

        private void DrawHeaderBanner(Graphics g)
        {
            Rectangle headerRect = new Rectangle(0, 0, ClientSize.Width, 42);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(14, 20, 38)))
            {
                g.FillRectangle(bg, headerRect);
            }
            using (Pen goldPen = new Pen(Color.FromArgb(218, 165, 32), 2))
            {
                g.DrawLine(goldPen, 0, 42, ClientSize.Width, 42);
            }

            using (Font titleFont = new Font("Segoe UI", 11.0f, FontStyle.Bold))
            using (SolidBrush goldBrush = new SolidBrush(Color.FromArgb(255, 223, 118)))
            {
                g.DrawString("SALA EXECUTIVA DE MODELOS LLM · AGENTE FRIDA", titleFont, goldBrush, 14, 10);
            }

            List<ModelRuntimeStats> statsList = ModelQuotaGuard.GetAllStats();
            int countActive = 0;
            int countIdle = 0;
            int countCooldown = 0;
            long reqToday = 0, failToday = 0, tokToday = 0;
            for (int i = 0; i < statsList.Count; i++)
            {
                ModelRuntimeStats s = statsList[i];
                if (s.IsActiveNow) countActive++;
                else if (s.IsCooldownActive()) countCooldown++;
                else countIdle++;
                reqToday += s.TotalRequestsToday;
                failToday += s.FailuresToday;
                tokToday += s.TokensInToday + s.TokensOutToday;
            }

            // Totais reais do dia (todas as funcoes: cerebro, voz, transcricao, visao, pesquisa)
            using (Font totFont = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (SolidBrush totBrush = new SolidBrush(Color.FromArgb(140, 230, 255)))
            {
                g.DrawString(string.Format("HOJE: {0} req  ·  {1} tokens  ·  {2} falhas", Num(reqToday), Num(tokToday), Num(failToday)),
                    totFont, totBrush, 470, 12);
            }

            int rx = ClientSize.Width - 360;
            int ry = 13;
            using (Font countFont = new Font("Segoe UI", 9.0f, FontStyle.Regular))
            using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(200, 220, 245)))
            {
                using (SolidBrush gDot = new SolidBrush(Color.FromArgb(50, 230, 110)))
                    g.FillEllipse(gDot, rx, ry + 3, 8, 8);
                g.DrawString(string.Format("{0} Em Uso", countActive), countFont, textBrush, rx + 12, ry);

                rx += 85;
                using (SolidBrush yDot = new SolidBrush(Color.FromArgb(255, 205, 40)))
                    g.FillEllipse(yDot, rx, ry + 3, 8, 8);
                g.DrawString(string.Format("{0} Prontos", countIdle), countFont, textBrush, rx + 12, ry);

                rx += 85;
                using (SolidBrush rDot = new SolidBrush(Color.FromArgb(255, 60, 60)))
                    g.FillEllipse(rDot, rx, ry + 3, 8, 8);
                g.DrawString(string.Format("{0} Esgotados", countCooldown), countFont, textBrush, rx + 12, ry);
            }
        }

        private void DrawRoomTabsBar(Graphics g)
        {
            List<ModelRuntimeStats> statsList = ModelQuotaGuard.GetAllStats();
            int countActive = 0;
            int countIdle = 0;
            int countCooldown = 0;
            for (int i = 0; i < statsList.Count; i++)
            {
                ModelRuntimeStats s = statsList[i];
                if (s.IsActiveNow) countActive++;
                else if (s.IsCooldownActive()) countCooldown++;
                else countIdle++;
            }

            string[] tabLabels = new string[]
            {
                "[1] Visão Geral",
                string.Format("[2] Escritório ({0})", countActive),
                string.Format("[3] Sala de Jogos ({0})", countIdle),
                string.Format("[4] Dormitório ({0})", countCooldown),
                "[5] Sala de Reuniões",
                "[6] Consumo & Histórico"
            };

            for (int t = 0; t < _roomTabRects.Length; t++)
            {
                Rectangle r = _roomTabRects[t];
                bool isSelected = ((int)_currentRoom == t);
                bool isHovered = (_hoveredTab == t);

                Color fill = isSelected ? Color.FromArgb(28, 48, 88) : (isHovered ? Color.FromArgb(22, 36, 66) : Color.FromArgb(14, 22, 42));
                using (SolidBrush b = new SolidBrush(fill))
                {
                    g.FillRectangle(b, r);
                }

                using (Pen borderPen = new Pen(isSelected ? Color.FromArgb(255, 215, 60) : Color.FromArgb(70, 95, 135), isSelected ? 2 : 1))
                {
                    g.DrawRectangle(borderPen, r);
                }

                using (Font tabFont = new Font("Segoe UI", 8.5f, isSelected ? FontStyle.Bold : FontStyle.Regular))
                using (SolidBrush textBrush = new SolidBrush(isSelected ? Color.FromArgb(255, 235, 120) : Color.FromArgb(195, 215, 240)))
                {
                    SizeF sz = g.MeasureString(tabLabels[t], tabFont);
                    float tx = r.X + (r.Width - sz.Width) / 2f;
                    float ty = r.Y + (r.Height - sz.Height) / 2f;
                    g.DrawString(tabLabels[t], tabFont, textBrush, tx, ty);
                }
            }
        }

        // =========================================================================
        // SALA 1: ESCRITÓRIO (Modelos Trabalhando para dj agora)
        // =========================================================================
        private void DrawOfficeRoom(Graphics g, Rectangle r)
        {
            // Parede azul aço executiva
            using (SolidBrush wall = new SolidBrush(Color.FromArgb(25, 38, 62)))
            {
                g.FillRectangle(wall, r.X, r.Y, r.Width, 110);
            }

            // Janelas de escritório com nuvens
            DrawExecutiveWindow(g, r.X + 180, r.Y + 12, 180, 75);
            DrawExecutiveWindow(g, r.X + 500, r.Y + 12, 180, 75);

            // Piso de madeira / parquet tecnológico
            DrawParquetFloor(g, r.X, r.Y + 110, r.Width, r.Height - 110);

            // Estações de trabalho (mesas de trabalho com monitores duplos e fios)
            int deskW = 140;
            int deskH = 55;
            for (int i = 0; i < 4; i++)
            {
                int dx = r.X + 50 + i * 195;
                int dy = r.Y + 195;
                DrawOfficeWorkstation(g, dx, dy, deskW, deskH);
            }

            // Servidor de Rack lateral piscando LEDs
            DrawServerRack(g, r.X + 16, r.Y + 95);

            // Cafeteira moderna com vapor
            DrawCoffeeStation(g, r.Right - 80, r.Y + 105);

            // Mensagem se ninguém estiver trabalhando no exato segundo
            List<ModelRuntimeStats> statsList = ModelQuotaGuard.GetAllStats();
            bool hasActive = false;
            for (int i = 0; i < statsList.Count; i++)
            {
                if (statsList[i].IsActiveNow) { hasActive = true; break; }
            }

            if (!hasActive)
            {
                Rectangle plaque = new Rectangle(r.X + 170, r.Y + 130, 520, 48);
                using (SolidBrush pBg = new SolidBrush(Color.FromArgb(220, 16, 26, 48)))
                using (Pen gold = new Pen(Color.FromArgb(218, 165, 32), 2))
                using (Font pf = new Font("Segoe UI", 9.5f, FontStyle.Bold))
                using (SolidBrush pt = new SolidBrush(Color.FromArgb(255, 225, 110)))
                {
                    g.FillRectangle(pBg, plaque);
                    g.DrawRectangle(gold, plaque);
                    string msg = "Escritorio em Standby: Nenhum modelo em inferencia no exato segundo.\nA equipe de modelos esta descansando ou na Sala de Jogos!";
                    g.DrawString(msg, pf, pt, plaque.X + 12, plaque.Y + 6);
                }
            }

            using (Pen borderPen = new Pen(Color.FromArgb(50, 75, 120), 2))
            {
                g.DrawRectangle(borderPen, r);
            }
        }

        private void DrawOfficeWorkstation(Graphics g, int x, int y, int w, int h)
        {
            // Mesa cinza grafite moderna
            using (SolidBrush desk = new SolidBrush(Color.FromArgb(55, 65, 80)))
            {
                g.FillRectangle(desk, x, y, w, h);
            }
            using (Pen edge = new Pen(Color.FromArgb(90, 110, 135), 2))
            {
                g.DrawRectangle(edge, x, y, w, h);
            }

            // Monitor Duplo (Azul / Ciano neon)
            using (SolidBrush scr1 = new SolidBrush(Color.FromArgb(15, 35, 60)))
            using (SolidBrush glow1 = new SolidBrush(Color.FromArgb(80, 200, 255)))
            {
                g.FillRectangle(scr1, x + 15, y - 24, 45, 24);
                g.FillRectangle(glow1, x + 18, y - 21, 39, 18);

                g.FillRectangle(scr1, x + 70, y - 24, 45, 24);
                g.FillRectangle(glow1, x + 73, y - 21, 39, 18);
            }

            // Teclado
            using (SolidBrush kb = new SolidBrush(Color.FromArgb(30, 35, 45)))
            {
                g.FillRectangle(kb, x + 35, y + 8, 55, 14);
            }
        }

        private void DrawServerRack(Graphics g, int x, int y)
        {
            using (SolidBrush rack = new SolidBrush(Color.FromArgb(20, 25, 35)))
            using (Pen p = new Pen(Color.FromArgb(70, 80, 100), 2))
            {
                g.FillRectangle(rack, x, y, 48, 80);
                g.DrawRectangle(p, x, y, 48, 80);
            }

            // LEDs piscantes
            for (int row = 0; row < 6; row++)
            {
                bool blink1 = ((_animTick + row * 4) % 15 < 8);
                bool blink2 = ((_animTick + row * 7) % 20 < 10);
                using (SolidBrush b1 = new SolidBrush(blink1 ? Color.FromArgb(0, 255, 150) : Color.FromArgb(0, 80, 40)))
                using (SolidBrush b2 = new SolidBrush(blink2 ? Color.FromArgb(0, 200, 255) : Color.FromArgb(0, 60, 90)))
                {
                    g.FillRectangle(b1, x + 8, y + 8 + row * 12, 6, 6);
                    g.FillRectangle(b2, x + 18, y + 8 + row * 12, 6, 6);
                }
            }
        }

        private void DrawCoffeeStation(Graphics g, int x, int y)
        {
            using (SolidBrush table = new SolidBrush(Color.FromArgb(120, 75, 40)))
            using (SolidBrush machine = new SolidBrush(Color.FromArgb(180, 50, 50)))
            {
                g.FillRectangle(table, x, y + 30, 60, 40);
                g.FillRectangle(machine, x + 10, y, 35, 32);
            }
            // Vapor
            float sY = (_animTick % 30);
            using (SolidBrush steam = new SolidBrush(Color.FromArgb(180, 240, 240, 240)))
            {
                g.FillEllipse(steam, x + 24, y - 8 - (int)(sY * 0.4f), 8, 12);
            }
        }

        // =========================================================================
        // SALA 2: SALA DE JOGOS (Modelos Ociosos / Standby)
        // =========================================================================
        private void DrawGameRoom(Graphics g, Rectangle r)
        {
            // Parede de tijolos retro aconchegante
            using (SolidBrush brickWall = new SolidBrush(Color.FromArgb(70, 36, 26)))
            {
                g.FillRectangle(brickWall, r.X, r.Y, r.Width, 110);
            }

            // Posters retro na parede
            DrawZeldaPoster(g, r.X + 60, r.Y + 20, 50, 65);
            DrawDartBoard(g, r.X + 160, r.Y + 30);

            // Piso de madeira nobre avermelhada
            DrawParquetFloor(g, r.X, r.Y + 110, r.Width, r.Height - 110);

            // Fliperamas 32-bit Arcade Cabinets (Esquerda)
            DrawArcadeCabinet(g, r.X + 45, r.Y + 130, "GROQ RACER", Color.FromArgb(0, 160, 240));
            DrawArcadeCabinet(g, r.X + 135, r.Y + 130, "ZELDA 32", Color.FromArgb(220, 50, 50));

            // Mesa de Sinuca / Pool Table no centro (Pano Verde Real)
            DrawPoolTable(g, r.X + 270, r.Y + 155, 280, 125);

            // Sofá Lounge Vermelho e mesa de petiscos (Direita)
            DrawLoungeSofa(g, r.Right - 220, r.Y + 160, 170, 70);
            DrawSnackTable(g, r.Right - 210, r.Y + 245);

            using (Pen borderPen = new Pen(Color.FromArgb(50, 75, 120), 2))
            {
                g.DrawRectangle(borderPen, r);
            }
        }

        private void DrawArcadeCabinet(Graphics g, int x, int y, string name, Color theme)
        {
            // Corpo do Fliperama
            using (SolidBrush cab = new SolidBrush(Color.FromArgb(30, 30, 40)))
            using (SolidBrush trim = new SolidBrush(theme))
            {
                g.FillRectangle(cab, x, y, 64, 110);
                g.FillRectangle(trim, x, y, 64, 18);
            }

            // Tela do Arcade piscando
            Color scrColor = ((_animTick % 20) < 10) ? Color.FromArgb(20, 120, 220) : Color.FromArgb(40, 160, 255);
            using (SolidBrush scr = new SolidBrush(scrColor))
            {
                g.FillRectangle(scr, x + 8, y + 24, 48, 38);
            }

            // Joystick & Botões
            using (SolidBrush joy = new SolidBrush(Color.Red))
            using (SolidBrush btn1 = new SolidBrush(Color.Yellow))
            using (SolidBrush btn2 = new SolidBrush(Color.Green))
            {
                g.FillEllipse(joy, x + 16, y + 70, 8, 8);
                g.FillEllipse(btn1, x + 36, y + 68, 6, 6);
                g.FillEllipse(btn2, x + 46, y + 72, 6, 6);
            }

            // Marquee Name
            using (Font mf = new Font("Segoe UI", 6.5f, FontStyle.Bold))
            using (SolidBrush w = new SolidBrush(Color.White))
            {
                g.DrawString(name, mf, w, x + 4, y + 2);
            }
        }

        private void DrawPoolTable(Graphics g, int x, int y, int w, int h)
        {
            // Borda de madeira nobre
            using (SolidBrush rim = new SolidBrush(Color.FromArgb(115, 60, 25)))
            {
                g.FillRectangle(rim, x, y, w, h);
            }
            // Pano verde
            using (SolidBrush felt = new SolidBrush(Color.FromArgb(25, 120, 50)))
            {
                g.FillRectangle(felt, x + 14, y + 14, w - 28, h - 28);
            }
            // 6 Caçapas pretas
            using (SolidBrush pocket = new SolidBrush(Color.Black))
            {
                g.FillEllipse(pocket, x + 10, y + 10, 14, 14);
                g.FillEllipse(pocket, x + w / 2 - 7, y + 10, 14, 14);
                g.FillEllipse(pocket, x + w - 24, y + 10, 14, 14);
                g.FillEllipse(pocket, x + 10, y + h - 24, 14, 14);
                g.FillEllipse(pocket, x + w / 2 - 7, y + h - 24, 14, 14);
                g.FillEllipse(pocket, x + w - 24, y + h - 24, 14, 14);
            }

            // Bolas de bilhar coloridas
            using (SolidBrush cueBall = new SolidBrush(Color.White))
            using (SolidBrush ball8 = new SolidBrush(Color.Black))
            using (SolidBrush ballRed = new SolidBrush(Color.Red))
            using (SolidBrush ballYel = new SolidBrush(Color.Yellow))
            {
                g.FillEllipse(cueBall, x + 60, y + h / 2, 8, 8);
                g.FillEllipse(ball8, x + w - 70, y + h / 2, 8, 8);
                g.FillEllipse(ballRed, x + w - 60, y + h / 2 - 6, 8, 8);
                g.FillEllipse(ballYel, x + w - 60, y + h / 2 + 6, 8, 8);
            }
        }

        private void DrawLoungeSofa(Graphics g, int x, int y, int w, int h)
        {
            using (SolidBrush sofa = new SolidBrush(Color.FromArgb(160, 45, 45)))
            using (SolidBrush pillow = new SolidBrush(Color.FromArgb(220, 180, 60)))
            {
                g.FillRectangle(sofa, x, y, w, h);
                g.FillRectangle(pillow, x + 10, y + 10, 24, 20);
                g.FillRectangle(pillow, x + w - 34, y + 10, 24, 20);
            }
        }

        private void DrawSnackTable(Graphics g, int x, int y)
        {
            using (SolidBrush t = new SolidBrush(Color.FromArgb(140, 85, 40)))
            using (SolidBrush pizza = new SolidBrush(Color.FromArgb(240, 180, 80)))
            using (SolidBrush soda = new SolidBrush(Color.FromArgb(220, 40, 40)))
            {
                g.FillEllipse(t, x, y, 70, 36);
                g.FillRectangle(pizza, x + 15, y + 8, 18, 14);
                g.FillRectangle(soda, x + 42, y + 6, 8, 14);
            }
        }

        private void DrawZeldaPoster(Graphics g, int x, int y, int w, int h)
        {
            using (SolidBrush p = new SolidBrush(Color.FromArgb(20, 50, 90)))
            using (SolidBrush triforce = new SolidBrush(Color.FromArgb(255, 215, 0)))
            {
                g.FillRectangle(p, x, y, w, h);
                Point[] tri = new Point[] { new Point(x + w / 2, y + 12), new Point(x + 14, y + 42), new Point(x + w - 14, y + 42) };
                g.FillPolygon(triforce, tri);
            }
        }

        private void DrawDartBoard(Graphics g, int x, int y)
        {
            using (SolidBrush outer = new SolidBrush(Color.FromArgb(30, 30, 30)))
            using (SolidBrush red = new SolidBrush(Color.Red))
            using (SolidBrush green = new SolidBrush(Color.Green))
            using (SolidBrush bullseye = new SolidBrush(Color.FromArgb(255, 215, 0)))
            {
                g.FillEllipse(outer, x, y, 44, 44);
                g.FillEllipse(red, x + 6, y + 6, 32, 32);
                g.FillEllipse(green, x + 12, y + 12, 20, 20);
                g.FillEllipse(bullseye, x + 18, y + 18, 8, 8);
            }
        }

        // =========================================================================
        // SALA 3: DORMITÓRIO / SALA DE DESCANSO (Modelos com Cota Excedida)
        // =========================================================================
        private void DrawBreakRoom(Graphics g, Rectangle r)
        {
            // Parede noturna estrelada suave
            using (SolidBrush nightWall = new SolidBrush(Color.FromArgb(22, 26, 48)))
            {
                g.FillRectangle(nightWall, r.X, r.Y, r.Width, 110);
            }

            // Janela noturna com lua crescente
            DrawNightWindow(g, r.X + 340, r.Y + 12, 160, 75);

            // Piso com carpete suave azul-meia-noite
            using (SolidBrush floor = new SolidBrush(Color.FromArgb(16, 22, 40)))
            {
                g.FillRectangle(floor, r.X, r.Y + 110, r.Width, r.Height - 110);
            }

            // 4 Camas de madeira confortáveis com travesseiros
            for (int i = 0; i < 4; i++)
            {
                int bx = r.X + 50 + i * 195;
                int by = r.Y + 180;
                DrawCozyBed(g, bx, by);
            }

            // Relógio Digital de Parede de Cooldown
            DrawCooldownDigitalClock(g, r.X + 80, r.Y + 30);

            // Se não há nenhum modelo em cooldown
            List<ModelRuntimeStats> statsList = ModelQuotaGuard.GetAllStats();
            bool hasCooldown = false;
            for (int i = 0; i < statsList.Count; i++)
            {
                if (statsList[i].IsCooldownActive()) { hasCooldown = true; break; }
            }

            if (!hasCooldown)
            {
                Rectangle plaque = new Rectangle(r.X + 170, r.Y + 130, 520, 48);
                using (SolidBrush pBg = new SolidBrush(Color.FromArgb(220, 16, 26, 48)))
                using (Pen gold = new Pen(Color.FromArgb(80, 200, 120), 2))
                using (Font pf = new Font("Segoe UI", 9.5f, FontStyle.Bold))
                using (SolidBrush pt = new SolidBrush(Color.FromArgb(160, 255, 180)))
                {
                    g.FillRectangle(pBg, plaque);
                    g.DrawRectangle(gold, plaque);
                    string msg = "Dormitorio Tranquilo: Nenhuma cota excedida hoje!\nTodos os modelos estao 100% descansados e com energia total.";
                    g.DrawString(msg, pf, pt, plaque.X + 12, plaque.Y + 6);
                }
            }

            using (Pen borderPen = new Pen(Color.FromArgb(50, 75, 120), 2))
            {
                g.DrawRectangle(borderPen, r);
            }
        }

        private void DrawCozyBed(Graphics g, int x, int y)
        {
            // Cabeceira da cama
            using (SolidBrush wood = new SolidBrush(Color.FromArgb(110, 65, 35)))
            {
                g.FillRectangle(wood, x, y, 76, 20);
                // Estrutura
                g.FillRectangle(wood, x, y + 20, 76, 85);
            }
            // Colchão & Lençol azul-violeta
            using (SolidBrush duvet = new SolidBrush(Color.FromArgb(75, 55, 130)))
            using (SolidBrush pillow = new SolidBrush(Color.FromArgb(240, 245, 255)))
            {
                g.FillRectangle(duvet, x + 6, y + 36, 64, 65);
                g.FillRectangle(pillow, x + 12, y + 18, 52, 18);
            }
            // Criado-mudo com abajur
            using (SolidBrush stand = new SolidBrush(Color.FromArgb(90, 50, 25)))
            using (SolidBrush lampGlow = new SolidBrush(Color.FromArgb(255, 230, 130)))
            {
                g.FillRectangle(stand, x + 82, y + 30, 24, 40);
                g.FillEllipse(lampGlow, x + 86, y + 16, 16, 16);
            }

            // Animação de "Zzz" subindo
            float zStep = (_animTick % 40);
            using (Font zf = new Font("Segoe UI", 8.0f + (zStep * 0.1f), FontStyle.Bold))
            using (SolidBrush zb = new SolidBrush(Color.FromArgb(220, 240, 255)))
            {
                g.DrawString("Z", zf, zb, x + 40 + (float)Math.Sin(zStep * 0.2f) * 6f, y - 6 - zStep * 0.7f);
            }
        }

        private void DrawNightWindow(Graphics g, int x, int y, int w, int h)
        {
            using (SolidBrush nightSky = new SolidBrush(Color.FromArgb(10, 16, 36)))
            using (SolidBrush moon = new SolidBrush(Color.FromArgb(255, 245, 180)))
            using (SolidBrush moonCut = new SolidBrush(Color.FromArgb(10, 16, 36)))
            {
                g.FillRectangle(nightSky, x, y, w, h);
                g.FillEllipse(moon, x + w - 45, y + 12, 30, 30);
                g.FillEllipse(moonCut, x + w - 52, y + 10, 28, 28);
            }
            using (Pen frame = new Pen(Color.FromArgb(110, 75, 45), 3))
            {
                g.DrawRectangle(frame, x, y, w, h);
                g.DrawLine(frame, x + w / 2, y, x + w / 2, y + h);
            }
        }

        private void DrawCooldownDigitalClock(Graphics g, int x, int y)
        {
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(15, 15, 20)))
            using (Pen p = new Pen(Color.FromArgb(60, 60, 80), 2))
            using (Font f = new Font("Consolas", 9.0f, FontStyle.Bold))
            using (SolidBrush text = new SolidBrush(Color.FromArgb(255, 60, 60)))
            {
                g.FillRectangle(bg, x, y, 190, 42);
                g.DrawRectangle(p, x, y, 190, 42);
                g.DrawString("ALARME DE COOLDOWN", new Font("Segoe UI", 6.5f, FontStyle.Bold), text, x + 8, y + 3);

                List<ModelRuntimeStats> statsList = ModelQuotaGuard.GetAllStats();
                TimeSpan maxCooldown = TimeSpan.Zero;
                for (int i = 0; i < statsList.Count; i++)
                {
                    if (statsList[i].IsCooldownActive())
                    {
                        TimeSpan rem = statsList[i].CooldownUntilUtc - DateTime.UtcNow;
                        if (rem > maxCooldown) maxCooldown = rem;
                    }
                }

                string cdText = (maxCooldown > TimeSpan.Zero)
                    ? string.Format("{0:00}m {1:00}s RESTANTES", Math.Max(0, maxCooldown.Minutes), Math.Max(0, maxCooldown.Seconds))
                    : "TODOS ACORDADOS [00:00]";

                g.DrawString(cdText, f, text, x + 8, y + 18);
            }
        }

        // =========================================================================
        // SALA 4: SALA DE REUNIÕES (Modelos Debatendo Consultas & Ações)
        // =========================================================================
        private void DrawMeetingRoom(Graphics g, Rectangle r)
        {
            // Parede executiva em mogno imperial
            using (SolidBrush wall = new SolidBrush(Color.FromArgb(48, 24, 18)))
            {
                g.FillRectangle(wall, r.X, r.Y, r.Width, 110);
            }

            // Janela do conclave
            DrawExecutiveWindow(g, r.X + 320, r.Y + 12, 220, 75);

            // Carpete azul marinho com medalhão dourado Triforce centralizado
            int carpetW = 540;
            int carpetH = 200;
            int carpetX = r.X + (r.Width - carpetW) / 2;
            int carpetY = r.Y + 115;
            DrawExecutiveCarpet(g, carpetX, carpetY, carpetW, carpetH);

            // Mesa circular de deliberação com microfones e cristal central
            int tableW = 340;
            int tableH = 130;
            int tableX = r.X + (r.Width - tableW) / 2;
            int tableY = r.Y + 150;
            DrawCircularMeetingTable(g, tableX, tableY, tableW, tableH);

            // Quadro Branco de Ata de Reunião na parede direita
            DrawMeetingWhiteboard(g, r.Right - 160, r.Y + 20, 140, 80);

            using (Pen borderPen = new Pen(Color.FromArgb(218, 165, 32), 2))
            {
                g.DrawRectangle(borderPen, r);
            }
        }

        private void DrawCircularMeetingTable(Graphics g, int x, int y, int w, int h)
        {
            // Sombra
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
            {
                g.FillEllipse(sh, x - 10, y + 10, w + 20, h + 20);
            }
            // Mesa de mogno
            using (SolidBrush wood = new SolidBrush(Color.FromArgb(145, 68, 30)))
            using (SolidBrush sheen = new SolidBrush(Color.FromArgb(175, 88, 42)))
            using (Pen gold = new Pen(Color.FromArgb(255, 215, 60), 2))
            {
                g.FillEllipse(wood, x, y, w, h);
                g.FillEllipse(sheen, x + 10, y + 10, w - 20, h - 20);
                g.DrawEllipse(gold, x + 6, y + 6, w - 12, h - 12);
            }

            // Selo da Triforce no centro
            int cx = x + w / 2;
            int cy = y + h / 2;
            using (SolidBrush tf = new SolidBrush(Color.FromArgb(255, 225, 80)))
            {
                Point[] tri = new Point[] { new Point(cx, cy - 14), new Point(cx - 16, cy + 12), new Point(cx + 16, cy + 12) };
                g.FillPolygon(tf, tri);
            }

            // Cristal Holo-Projetor Central
            float floatPulse = (float)Math.Sin(_animTick * 0.2f) * 3f;
            using (SolidBrush crystal = new SolidBrush(Color.FromArgb(0, 240, 255)))
            {
                g.FillEllipse(crystal, cx - 6, cy - 6 + (int)floatPulse, 12, 12);
            }
        }

        private void DrawMeetingWhiteboard(Graphics g, int x, int y, int w, int h)
        {
            using (SolidBrush wb = new SolidBrush(Color.FromArgb(245, 248, 252)))
            using (Pen frame = new Pen(Color.FromArgb(160, 175, 195), 2))
            using (Font f = new Font("Segoe UI", 7.0f, FontStyle.Bold))
            using (SolidBrush text = new SolidBrush(Color.FromArgb(20, 40, 80)))
            {
                g.FillRectangle(wb, x, y, w, h);
                g.DrawRectangle(frame, x, y, w, h);
                g.DrawString("PAUTA DE DELIBERAÇÃO", f, text, x + 6, y + 4);
                g.DrawLine(Pens.LightGray, x + 6, y + 18, x + w - 6, y + 18);

                using (Font itemF = new Font("Segoe UI", 6.5f))
                {
                    g.DrawString("• Proposta (Batedor Groq)", itemF, text, x + 8, y + 24);
                    g.DrawString("• Auditoria (Tático Qwen)", itemF, text, x + 8, y + 38);
                    g.DrawString("• Síntese (Mago Gemini)", itemF, text, x + 8, y + 52);
                }
            }
        }

        // =========================================================================
        // SALA 5: VISÃO GERAL (Planta 2x2 com todas as salas interligadas)
        // =========================================================================
        private void DrawOverviewRoom(Graphics g, Rectangle r)
        {
            int midX = r.X + r.Width / 2;
            int midY = r.Y + r.Height / 2;

            Rectangle qOffice = new Rectangle(r.X, r.Y, r.Width / 2 - 2, r.Height / 2 - 2);
            Rectangle qGame = new Rectangle(midX + 2, r.Y, r.Width / 2 - 2, r.Height / 2 - 2);
            Rectangle qBreak = new Rectangle(r.X, midY + 2, r.Width / 2 - 2, r.Height / 2 - 2);
            Rectangle qMeeting = new Rectangle(midX + 2, midY + 2, r.Width / 2 - 2, r.Height / 2 - 2);

            // Desenha os 4 quadrantes, cada um recortado na propria area (a arte e feita para a sala inteira
            // e antes transbordava por cima dos outros quadrantes, do HUD e do painel lateral)
            Region previousClip = g.Clip;
            g.SetClip(qOffice); DrawOfficeRoom(g, qOffice);
            g.SetClip(qGame); DrawGameRoom(g, qGame);
            g.SetClip(qBreak); DrawBreakRoom(g, qBreak);
            g.SetClip(qMeeting); DrawMeetingRoom(g, qMeeting);
            g.Clip = previousClip;

            // Rótulos dos Quadrantes
            using (Font qFont = new Font("Segoe UI", 8.0f, FontStyle.Bold))
            using (SolidBrush qBg = new SolidBrush(Color.FromArgb(210, 10, 15, 30)))
            using (SolidBrush qText = new SolidBrush(Color.FromArgb(255, 230, 120)))
            {
                DrawBadge(g, qOffice.X + 8, qOffice.Y + 8, "1. ESCRITÓRIO (EM USO)", qFont, qBg, qText);
                DrawBadge(g, qGame.X + 8, qGame.Y + 8, "2. SALA DE JOGOS (STANDBY)", qFont, qBg, qText);
                DrawBadge(g, qBreak.X + 8, qBreak.Y + 8, "3. DORMITÓRIO (COTA / COCHILO)", qFont, qBg, qText);
                DrawBadge(g, qMeeting.X + 8, qMeeting.Y + 8, "4. SALA DE REUNIÕES (CONCLAVE)", qFont, qBg, qText);
            }

            // Portas e paredes divisórias
            using (Pen divPen = new Pen(Color.FromArgb(218, 165, 32), 3))
            {
                g.DrawLine(divPen, midX, r.Y, midX, r.Bottom);
                g.DrawLine(divPen, r.X, midY, r.Right, midY);
            }

            // Porta aberta no centro
            using (SolidBrush door = new SolidBrush(Color.FromArgb(14, 20, 36)))
            {
                g.FillRectangle(door, midX - 25, midY - 14, 50, 28);
            }
        }

        private void DrawBadge(Graphics g, int x, int y, string text, Font f, Brush bg, Brush textB)
        {
            SizeF sz = g.MeasureString(text, f);
            g.FillRectangle(bg, x, y, (int)sz.Width + 8, (int)sz.Height + 4);
            using (Pen p = new Pen(Color.FromArgb(218, 165, 32), 1))
            {
                g.DrawRectangle(p, x, y, (int)sz.Width + 8, (int)sz.Height + 4);
            }
            g.DrawString(text, f, textB, x + 4, y + 2);
        }

        private void DrawCharactersForCurrentRoom(Graphics g)
        {
            List<ModelRuntimeStats> statsList = ModelQuotaGuard.GetAllStats();

            for (int i = 0; i < _characterSlots.Length; i++)
            {
                if (!_slotActiveInRoom[i]) continue;

                Rectangle slot = _characterSlots[i];
                ModelRuntimeStats s = (i < statsList.Count) ? statsList[i] : null;
                bool isHovered = (i == _hoveredCharIndex);
                bool isSelected = (i == _selectedCharIndex);
                ZeldaActorState a = (i < _actors.Length) ? _actors[i] : null;

                int cx = (a != null) ? (int)a.CurrX : (slot.X + slot.Width / 2);
                int cy = (a != null) ? (int)a.CurrY : slot.Y;

                // Sombra suave sob os pés do personagem
                using (SolidBrush shadowB = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                {
                    g.FillEllipse(shadowB, cx - 18, cy + 54, 36, 12);
                }

                if (isSelected || isHovered)
                {
                    DrawZeldaSelectorRing(g, cx, cy + 56, isSelected);
                }

                string spriteKey = (s != null) ? s.SpriteKey : "scout";
                int facing = (a != null) ? a.Facing : 0;
                int walkFrame = (a != null) ? a.WalkFrame : 1;
                bool isMoving = (a != null) ? a.IsMoving : false;

                DrawPixelCharacterSprite(g, cx - 32, cy, spriteKey, isHovered, isSelected, facing, walkFrame, isMoving);

                DrawFloatingStatusGem(g, cx, cy - 8, s);

                DrawCharacterMiniTag(g, cx, cy + 62, s, isSelected);

                if (a != null && !string.IsNullOrEmpty(a.SpeechBubble))
                {
                    DrawSpeechBubble(g, cx, cy - 14, a.SpeechBubble);
                }
            }
        }

        // =========================================================================
        // HUD DA SALA DE REUNIÕES (Deliberação Colaborativa entre Modelos)
        // =========================================================================
        private void DrawMeetingHud(Graphics g)
        {
            Rectangle hudRect = HudRect;

            using (SolidBrush hudBg = new SolidBrush(Color.FromArgb(12, 18, 36)))
            {
                g.FillRectangle(hudBg, hudRect);
            }

            DrawZeldaOrnamentalBorder(g, hudRect);

            // Linha 1: Input do Tema e Botões Rápidos
            using (Font titleF = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (SolidBrush goldB = new SolidBrush(Color.FromArgb(255, 220, 80)))
            {
                g.DrawString("Tema da Reunião:", titleF, goldB, hudRect.X + 16, hudRect.Y + 14);
            }

            // Botão Iniciar Reunião
            _btnStartMeetingRect = new Rectangle(hudRect.Right - 180, hudRect.Y + 12, 164, 26);
            string meetingBtnText = _isMeetingRunning ? "Debatendo..." : "[► Iniciar Reuniao]";
            DrawZeldaButton(g, _btnStartMeetingRect, meetingBtnText, _isHoveringStartMeetingBtn, _isMeetingRunning ? Color.FromArgb(180, 80, 20) : Color.FromArgb(30, 100, 180));

            // Botões Rápidos de Pauta
            int qY = hudRect.Y + 42;
            _btnQuickTopic1Rect = new Rectangle(hudRect.X + 16, qY, 175, 22);
            _btnQuickTopic2Rect = new Rectangle(_btnQuickTopic1Rect.Right + 8, qY, 185, 22);
            _btnQuickTopic3Rect = new Rectangle(_btnQuickTopic2Rect.Right + 8, qY, 195, 22);

            DrawZeldaButton(g, _btnQuickTopic1Rect, "1. Otimizar Arquitetura", false, Color.FromArgb(20, 45, 80));
            DrawZeldaButton(g, _btnQuickTopic2Rect, "2. Estratégia sem Recusa", false, Color.FromArgb(20, 45, 80));
            DrawZeldaButton(g, _btnQuickTopic3Rect, "3. Resiliência de Cotas", false, Color.FromArgb(20, 45, 80));

            // Transcrição dos discursos dos modelos em formato retro
            Rectangle logBox = new Rectangle(hudRect.X + 16, qY + 28, hudRect.Width - 32, hudRect.Bottom - (qY + 28) - 14);
            using (SolidBrush logBg = new SolidBrush(Color.FromArgb(8, 12, 24)))
            using (Pen logBorder = new Pen(Color.FromArgb(50, 75, 120), 1))
            {
                g.FillRectangle(logBg, logBox);
                g.DrawRectangle(logBorder, logBox);
            }

            lock (_meetingTranscript)
            {
                if (_meetingTranscript.Count == 0 && !_isMeetingRunning)
                {
                    using (Font hintF = new Font("Segoe UI", 9.0f, FontStyle.Italic))
                    using (SolidBrush hintB = new SolidBrush(Color.FromArgb(160, 180, 210)))
                    {
                        string msg = "A Sala de Reuniões está pronta. Digite qualquer pauta acima ou clique em [Iniciar Reunião].\nOs modelos (Groq 120B, Qwen 27B, Cerebras e Gemini) debaterão em rodadas sucessivas para definir a melhor solução para você!";
                        g.DrawString(msg, hintF, hintB, logBox.X + 14, logBox.Y + 36);
                    }
                }
                else
                {
                    int lineY = logBox.Y + 8;
                    for (int i = 0; i < _meetingTranscript.Count; i++)
                    {
                        if (lineY > logBox.Bottom - 26) break;

                        MeetingSpeechTurn turn = _meetingTranscript[i];

                        // Retrato mini
                        DrawPixelCharacterSprite(g, logBox.X + 8, lineY - 8, turn.SpriteKey, false, false);

                        using (Font nameF = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                        using (Font argF = new Font("Segoe UI", 8.5f))
                        using (SolidBrush nameB = new SolidBrush(turn.ThemeColor))
                        using (SolidBrush argB = new SolidBrush(Color.White))
                        {
                            g.DrawString(turn.SpeakerTitle + ":", nameF, nameB, logBox.X + 54, lineY);
                            g.DrawString(TruncateText(turn.Argument, 130), argF, argB, logBox.X + 54, lineY + 16);
                        }

                        lineY += 34;
                    }
                }
            }
        }

        // =========================================================================
        // HUD PADRÃO ZELDA (Telemetria, Cotas, Consumo e Ações)
        // =========================================================================
        private void DrawExecutiveWindow(Graphics g, int wx, int wy, int ww, int wh)
        {
            Rectangle winRect = new Rectangle(wx, wy, ww, wh);
            using (SolidBrush skyBrush = new SolidBrush(Color.FromArgb(70, 130, 210)))
            {
                g.FillRectangle(skyBrush, winRect);
            }

            using (SolidBrush cloudBrush = new SolidBrush(Color.FromArgb(230, 245, 255)))
            {
                int cloudY = wy + 20;
                float c1 = (wx + _cloudOffset) % (ww + 80) - 40;
                float c2 = (wx + _cloudOffset * 0.6f + 120) % (ww + 80) - 40;

                DrawPixelCloud(g, wx + (int)c1, cloudY, cloudBrush);
                DrawPixelCloud(g, wx + (int)c2, cloudY + 18, cloudBrush);
            }

            using (SolidBrush cityBrush = new SolidBrush(Color.FromArgb(40, 75, 130)))
            {
                g.FillRectangle(cityBrush, wx + 10, wy + wh - 22, 28, 22);
                g.FillRectangle(cityBrush, wx + 44, wy + wh - 30, 24, 30);
                g.FillRectangle(cityBrush, wx + 74, wy + wh - 18, 32, 18);
                g.FillRectangle(cityBrush, wx + 112, wy + wh - 36, 20, 36);
                g.FillRectangle(cityBrush, wx + 138, wy + wh - 26, 36, 26);
                g.FillRectangle(cityBrush, wx + 180, wy + wh - 32, 22, 32);
            }

            using (Pen framePen = new Pen(Color.FromArgb(140, 95, 45), 4))
            {
                g.DrawRectangle(framePen, winRect);
                g.DrawLine(framePen, wx + ww / 2, wy, wx + ww / 2, wy + wh);
                g.DrawLine(framePen, wx, wy + wh / 2, wx + ww, wy + wh / 2);
            }
        }

        private void DrawPixelCloud(Graphics g, int cx, int cy, Brush b)
        {
            g.FillRectangle(b, cx + 8, cy, 32, 8);
            g.FillRectangle(b, cx + 4, cy + 6, 40, 10);
            g.FillRectangle(b, cx, cy + 10, 48, 8);
        }

        private void DrawParquetFloor(Graphics g, int fx, int fy, int fw, int fh)
        {
            Color c1 = Color.FromArgb(120, 76, 42);
            Color c2 = Color.FromArgb(108, 66, 36);
            int tileSize = 24;

            for (int y = fy; y < fy + fh; y += tileSize)
            {
                for (int x = fx; x < fx + fw; x += tileSize)
                {
                    bool alt = ((x / tileSize) + (y / tileSize)) % 2 == 0;
                    using (SolidBrush b = new SolidBrush(alt ? c1 : c2))
                    {
                        int rw = Math.Min(tileSize, fx + fw - x);
                        int rh = Math.Min(tileSize, fy + fh - y);
                        g.FillRectangle(b, x, y, rw, rh);
                    }
                }
            }
        }

        private void DrawExecutiveCarpet(Graphics g, int rx, int ry, int rw, int rh)
        {
            Rectangle crp = new Rectangle(rx, ry, rw, rh);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(24, 45, 90)))
            {
                g.FillRectangle(b, crp);
            }
            using (Pen goldBorder = new Pen(Color.FromArgb(218, 165, 32), 3))
            {
                g.DrawRectangle(goldBorder, crp);
            }
            using (Pen goldInner = new Pen(Color.FromArgb(180, 130, 20), 1))
            {
                g.DrawRectangle(goldInner, rx + 6, ry + 6, rw - 12, rh - 12);
            }
        }

        private void DrawZeldaSelectorRing(Graphics g, int cx, int cy, bool isSelected)
        {
            int rx = isSelected ? 36 : 30;
            int ry = isSelected ? 14 : 12;

            float pulse = (float)Math.Sin(_animTick * 0.15f) * 2f;
            rx += (int)pulse;

            using (Pen p = new Pen(isSelected ? Color.FromArgb(255, 220, 60) : Color.FromArgb(130, 200, 255), isSelected ? 2f : 1.5f))
            {
                g.DrawEllipse(p, cx - rx, cy - ry, rx * 2, ry * 2);
            }

            if (isSelected)
            {
                using (SolidBrush gold = new SolidBrush(Color.FromArgb(255, 230, 80)))
                {
                    g.FillPolygon(gold, new Point[] { new Point(cx, cy - ry - 4), new Point(cx - 5, cy - ry - 10), new Point(cx + 5, cy - ry - 10) });
                }
            }
        }

        private void DrawPixelCharacterSprite(Graphics g, int x, int y, string spriteKey, bool isHovered, bool isSelected)
        {
            DrawPixelCharacterSprite(g, x, y, spriteKey, isHovered, isSelected, 0, 1, false);
        }

        private void DrawPixelCharacterSprite(Graphics g, int x, int y, string spriteKey, bool isHovered, bool isSelected, int facing, int walkFrame, bool isMoving)
        {
            // Efeito de caminhada ou respiração idle
            int bob = 0;
            int leftBootOffset = 0;
            int rightBootOffset = 0;

            if (isMoving)
            {
                if (walkFrame == 0)
                {
                    bob = -1;
                    leftBootOffset = -3;
                    rightBootOffset = 3;
                }
                else if (walkFrame == 2)
                {
                    bob = 1;
                    leftBootOffset = 3;
                    rightBootOffset = -3;
                }
            }
            else
            {
                bob = ((_animTick / 10) % 2 == 0) ? 0 : 1;
            }

            int cy = y + bob;

            Color skinColor = Color.FromArgb(255, 218, 185);
            Color eyeColor = Color.FromArgb(20, 30, 50);

            Color tunicColor = Color.FromArgb(34, 139, 34);
            Color hairColor = Color.FromArgb(184, 115, 51);
            Color accColor = Color.FromArgb(255, 215, 0);

            if (spriteKey == "tactician")
            {
                tunicColor = Color.FromArgb(30, 90, 180);
                hairColor = Color.FromArgb(45, 30, 20);
                accColor = Color.FromArgb(220, 220, 240);
            }
            else if (spriteKey == "titan")
            {
                tunicColor = Color.FromArgb(160, 82, 45);
                hairColor = Color.FromArgb(80, 80, 90);
                accColor = Color.FromArgb(255, 140, 0);
            }
            else if (spriteKey == "scholar")
            {
                tunicColor = Color.FromArgb(75, 0, 130);
                hairColor = Color.FromArgb(220, 230, 255);
                accColor = Color.FromArgb(0, 191, 255);
            }
            else if (spriteKey == "priestess")
            {
                tunicColor = Color.FromArgb(235, 130, 180);
                hairColor = Color.FromArgb(245, 210, 85);
                accColor = Color.FromArgb(255, 223, 128);
            }
            else if (spriteKey == "oracle")
            {
                tunicColor = Color.FromArgb(64, 180, 200);
                hairColor = Color.FromArgb(255, 240, 180);
                accColor = Color.FromArgb(180, 255, 250);
            }
            else if (spriteKey == "scribe")
            {
                tunicColor = Color.FromArgb(178, 34, 34);
                hairColor = Color.FromArgb(30, 30, 30);
                accColor = Color.FromArgb(80, 200, 120);
            }
            else if (spriteKey == "herald")
            {
                tunicColor = Color.FromArgb(40, 160, 220);
                hairColor = Color.FromArgb(230, 230, 240);
                accColor = Color.FromArgb(255, 215, 0);
            }

            // --- CABELO E CABEÇA BASE ---
            using (SolidBrush hb = new SolidBrush(hairColor))
            {
                g.FillRectangle(hb, x + 16, cy, 32, 26);
                if (spriteKey == "priestess" || spriteKey == "oracle")
                {
                    g.FillRectangle(hb, x + 12, cy + 12, 10, 32);
                    g.FillRectangle(hb, x + 42, cy + 12, 10, 32);
                }
            }

            // Se estiver de costas (facing == 1), cabelo cobre a cabeça inteira
            if (facing == 1)
            {
                using (SolidBrush hb = new SolidBrush(hairColor))
                {
                    g.FillRectangle(hb, x + 18, cy + 6, 28, 22);
                    g.FillRectangle(hb, x + 20, cy + 24, 24, 6);
                }
            }
            else
            {
                // Rosto (Pele)
                using (SolidBrush sb = new SolidBrush(skinColor))
                {
                    g.FillRectangle(sb, x + 20, cy + 8, 24, 20);
                    g.FillRectangle(sb, x + 16, cy + 14, 4, 8);
                    g.FillRectangle(sb, x + 44, cy + 14, 4, 8);
                }

                // Olhos de acordo com a direção (facing 0=frente, 2=esquerda, 3=direita)
                bool isBlinking = (!isMoving && ((_animTick % 90) > 85));
                using (SolidBrush eb = new SolidBrush(eyeColor))
                using (SolidBrush white = new SolidBrush(Color.White))
                {
                    int eyeLeftX = x + 24;
                    int eyeRightX = x + 36;

                    if (facing == 2) // Olhando para a esquerda
                    {
                        eyeLeftX = x + 20;
                        eyeRightX = x + 28;
                    }
                    else if (facing == 3) // Olhando para a direita
                    {
                        eyeLeftX = x + 32;
                        eyeRightX = x + 40;
                    }

                    if (isBlinking)
                    {
                        g.FillRectangle(eb, eyeLeftX, cy + 18, 4, 2);
                        g.FillRectangle(eb, eyeRightX, cy + 18, 4, 2);
                    }
                    else
                    {
                        g.FillRectangle(eb, eyeLeftX, cy + 16, 4, 6);
                        g.FillRectangle(eb, eyeRightX, cy + 16, 4, 6);
                        g.FillRectangle(white, eyeLeftX, cy + 16, 2, 2);
                        g.FillRectangle(white, eyeRightX, cy + 16, 2, 2);
                    }
                }

                // Franja do Cabelo
                using (SolidBrush hb = new SolidBrush(hairColor))
                {
                    g.FillRectangle(hb, x + 18, cy + 2, 28, 8);
                    g.FillRectangle(hb, x + 18, cy + 10, 6, 8);
                    g.FillRectangle(hb, x + 40, cy + 10, 6, 8);
                }
            }

            // Chapéus e Acessórios de Cabeça
            if (spriteKey == "scout")
            {
                using (SolidBrush cap = new SolidBrush(Color.FromArgb(20, 100, 25)))
                using (SolidBrush lens = new SolidBrush(Color.FromArgb(0, 255, 200)))
                {
                    g.FillRectangle(cap, x + 18, cy + 2, 28, 6);
                    if (facing != 1) g.FillRectangle(lens, x + 22, cy + 6, 20, 4);
                }
            }
            else if (spriteKey == "priestess")
            {
                using (SolidBrush gold = new SolidBrush(Color.FromArgb(255, 220, 80)))
                {
                    g.FillRectangle(gold, x + 22, cy + 4, 20, 4);
                    if (facing != 1) g.FillPolygon(gold, new Point[] { new Point(x + 32, cy + 1), new Point(x + 29, cy + 4), new Point(x + 35, cy + 4) });
                }
            }
            else if (spriteKey == "scholar")
            {
                using (SolidBrush hat = new SolidBrush(Color.FromArgb(50, 0, 100)))
                using (SolidBrush gold = new SolidBrush(Color.FromArgb(255, 215, 0)))
                {
                    Point[] hatPoly = new Point[] { new Point(x + 32, cy - 14), new Point(x + 14, cy + 4), new Point(x + 50, cy + 4) };
                    g.FillPolygon(hat, hatPoly);
                    g.FillRectangle(gold, x + 30, cy - 8, 4, 4);
                }
            }
            else if (spriteKey == "titan")
            {
                using (SolidBrush metal = new SolidBrush(Color.FromArgb(190, 160, 100)))
                {
                    g.FillRectangle(metal, x + 14, cy + 2, 6, 12);
                    g.FillRectangle(metal, x + 44, cy + 2, 6, 12);
                }
            }

            // Túnica do Tronco
            using (SolidBrush tb = new SolidBrush(tunicColor))
            {
                g.FillRectangle(tb, x + 18, cy + 28, 28, 28);
            }

            // Acessórios do Peito / Manto
            using (SolidBrush ab = new SolidBrush(accColor))
            {
                g.FillRectangle(ab, x + 20, cy + 40, 24, 4);

                if (spriteKey == "titan")
                {
                    float corePulse = (float)Math.Sin(_animTick * 0.2f);
                    Color glowC = (corePulse > 0) ? Color.FromArgb(255, 180, 0) : Color.FromArgb(255, 80, 0);
                    using (SolidBrush core = new SolidBrush(glowC))
                    {
                        g.FillRectangle(core, x + 28, cy + 32, 8, 8);
                    }
                }
                else if (spriteKey == "priestess")
                {
                    using (Pen harpPen = new Pen(Color.FromArgb(255, 220, 80), 2))
                    {
                        g.DrawArc(harpPen, x + 42, cy + 30, 12, 18, 180, 220);
                        g.DrawLine(harpPen, x + 44, cy + 32, x + 44, cy + 46);
                    }
                }
                else if (spriteKey == "scribe")
                {
                    using (SolidBrush sc = new SolidBrush(Color.FromArgb(240, 230, 200)))
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(40, 40, 40)))
                    {
                        g.FillRectangle(sc, x + 44, cy + 32, 8, 14);
                        g.FillRectangle(br, x + 12, cy + 32, 4, 14);
                    }
                }
                else if (spriteKey == "scholar")
                {
                    float orbFloat = (float)Math.Sin(_animTick * 0.25f) * 3f;
                    using (SolidBrush orbB = new SolidBrush(Color.FromArgb(120, 220, 255)))
                    {
                        g.FillEllipse(orbB, x + 44, cy + 24 + (int)orbFloat, 10, 10);
                    }
                }
            }

            // Botas de Aventura (Com passo alternado durante caminhada)
            using (SolidBrush bootBrush = new SolidBrush(Color.FromArgb(70, 40, 20)))
            {
                g.FillRectangle(bootBrush, x + 20, cy + 56 + leftBootOffset, 10, 8);
                g.FillRectangle(bootBrush, x + 34, cy + 56 + rightBootOffset, 10, 8);
            }
        }

        private void DrawFloatingStatusGem(Graphics g, int cx, int cy, ModelRuntimeStats stats)
        {
            if (stats == null) return;

            float floatY = (float)Math.Sin((_animTick + cx) * 0.12f) * 4f;
            int gy = cy + (int)floatY;

            bool isCooldown = stats.IsCooldownActive();
            bool isActive = stats.IsActiveNow;

            Color gemCore;
            Color gemGlow;
            string statusBadge;

            if (isActive)
            {
                gemCore = Color.FromArgb(0, 240, 100);
                gemGlow = Color.FromArgb(120, 0, 255, 120);
                statusBadge = "EM USO";
            }
            else if (isCooldown)
            {
                gemCore = ((_animTick % 20) < 10) ? Color.FromArgb(255, 40, 40) : Color.FromArgb(200, 20, 20);
                gemGlow = Color.FromArgb(140, 255, 0, 0);
                statusBadge = "COTA!";
            }
            else
            {
                gemCore = Color.FromArgb(255, 200, 30);
                gemGlow = Color.FromArgb(90, 255, 215, 0);
                statusBadge = "PRONTO";
            }

            using (SolidBrush aura = new SolidBrush(gemGlow))
            {
                g.FillEllipse(aura, cx - 12, gy - 12, 24, 24);
            }

            Point[] diamond = new Point[]
            {
                new Point(cx, gy - 8),
                new Point(cx + 6, gy),
                new Point(cx, gy + 8),
                new Point(cx - 6, gy)
            };
            using (SolidBrush b = new SolidBrush(gemCore))
            {
                g.FillPolygon(b, diamond);
            }
            using (Pen border = new Pen(Color.White, 1))
            {
                g.DrawPolygon(border, diamond);
            }

            using (Font miniFont = new Font("Segoe UI", 6.75f, FontStyle.Bold))
            using (SolidBrush bgB = new SolidBrush(Color.FromArgb(200, 10, 15, 25)))
            using (SolidBrush textB = new SolidBrush(gemCore))
            {
                SizeF sz = g.MeasureString(statusBadge, miniFont);
                int bx = cx - (int)(sz.Width / 2);
                int by = gy - 18;
                g.FillRectangle(bgB, bx - 2, by, (int)sz.Width + 4, (int)sz.Height);
                g.DrawString(statusBadge, miniFont, textB, bx, by);
            }
        }

        private void DrawDetailedPixelPortrait(Graphics g, Rectangle r, string spriteKey)
        {
            int cx = r.X + r.Width / 2;
            int cy = r.Y + r.Height / 2 - 4;
            DrawPixelCharacterSprite(g, cx - 34, cy - 28, spriteKey, false, false);
        }

        private void DrawZeldaOrnamentalBorder(Graphics g, Rectangle r)
        {
            using (Pen goldOuter = new Pen(Color.FromArgb(218, 165, 32), 3))
            {
                g.DrawRectangle(goldOuter, r);
            }
            using (Pen goldInner = new Pen(Color.FromArgb(140, 100, 20), 1))
            {
                g.DrawRectangle(goldInner, r.X + 4, r.Y + 4, r.Width - 8, r.Height - 8);
            }

            DrawCornerGem(g, r.X + 2, r.Y + 2);
            DrawCornerGem(g, r.Right - 10, r.Y + 2);
            DrawCornerGem(g, r.X + 2, r.Bottom - 10);
            DrawCornerGem(g, r.Right - 10, r.Bottom - 10);
        }

        private void DrawCornerGem(Graphics g, int x, int y)
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 215, 0)))
            {
                g.FillRectangle(b, x, y, 8, 8);
            }
            using (SolidBrush inner = new SolidBrush(Color.FromArgb(160, 40, 40)))
            {
                g.FillRectangle(inner, x + 2, y + 2, 4, 4);
            }
        }

        private void DrawZeldaButton(Graphics g, Rectangle r, string text, bool isHovered, Color baseColor)
        {
            Color fill = isHovered ? Color.FromArgb(Math.Min(255, baseColor.R + 40), Math.Min(255, baseColor.G + 40), Math.Min(255, baseColor.B + 40)) : baseColor;
            using (SolidBrush b = new SolidBrush(fill))
            {
                g.FillRectangle(b, r);
            }
            using (Pen goldBorder = new Pen(isHovered ? Color.FromArgb(255, 235, 120) : Color.FromArgb(200, 160, 40), 2))
            {
                g.DrawRectangle(goldBorder, r);
            }

            using (Font btnFont = new Font("Segoe UI", 8.25f, FontStyle.Bold))
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                SizeF sz = g.MeasureString(text, btnFont);
                float tx = r.X + (r.Width - sz.Width) / 2f;
                float ty = r.Y + (r.Height - sz.Height) / 2f;
                g.DrawString(text, btnFont, textBrush, tx, ty);
            }
        }

        private static string TruncateText(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return (text.Length > max) ? (text.Substring(0, max) + "...") : text;
        }
    }
}
